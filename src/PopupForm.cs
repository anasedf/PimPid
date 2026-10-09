using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// ป๊อปอัปจากถาดระบบ: layered window วาดเองทั้งหมด (ขอบโปร่งใส + มาสคอตโผล่พ้นการ์ด)
class PopupForm : Form
{
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }

    internal static readonly Color CardBg = Color.FromArgb(28, 28, 33);
    internal static readonly Color Border = Color.FromArgb(50, 50, 58);
    static readonly Color Divider = Color.FromArgb(42, 42, 49);
    internal static readonly Color Text1 = Color.FromArgb(236, 236, 241);
    internal static readonly Color Text2 = Color.FromArgb(154, 154, 166);
    static readonly Color HoverBg = Color.FromArgb(38, 38, 45);
    static readonly Color Accent = Color.FromArgb(232, 116, 59);
    static readonly Color Blue = Color.FromArgb(59, 130, 246);
    static readonly Color Green = Color.FromArgb(34, 197, 94);
    static readonly Color SwitchOff = Color.FromArgb(63, 63, 72);

    const int W = 340;
    const float CardX = 8, CardY = 48, CardW = W - 16;

    readonly MainForm app;
    readonly Timer timer = new Timer { Interval = 15 };
    readonly LayeredSurface surface = new LayeredSurface();
    readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();
    const double AppearMs = 170;
    readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
    readonly List<KeyValuePair<RectangleF, Action>> hits = new List<KeyValuePair<RectangleF, Action>>();
    float scale = 1f;
    int logicalH;
    int hover = -1, contentHits;
    double nowMs, bubbleUntilMs = -1;
    Bitmap cardBmp;          // การ์ดที่วาดไว้แล้ว (ไม่รวมแมว) ใช้ซ้ำจนกว่าจะมีอะไรเปลี่ยน
    bool dirty = true;
    string overlayKey;       // ท่าทางแมวในเฟรมล่าสุด
    byte lastAlpha;
    int lastDy = -1;
    bool capturing;          // กำลังรอให้กดปุ่มลัดใหม่
    string captureText;      // ข้อความที่โชว์ระหว่างรอ
    bool captureError;
    float appear;
    DateTime hiddenAt;

    public PopupForm(MainForm app)
    {
        this.app = app;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
        timer.Tick += delegate { Frame(); };
        logicalH = MeasureHeight();
        BuildCard(null).Dispose(); // อุ่นเครื่อง (โหลดฟอนต์/JIT) ตั้งแต่เปิดแอป ครั้งแรกจะได้ไม่หน่วง
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80; // WS_EX_LAYERED | WS_EX_TOOLWINDOW
            return cp;
        }
    }

    // ---- เปิด/ปิด ----
    public void Toggle()
    {
        if (Visible) { HideNow(); return; }
        if ((DateTime.Now - hiddenAt).TotalMilliseconds < 300) return; // เพิ่งปิดเพราะคลิกไอคอน
        ShowNow();
    }

    void ShowNow()
    {
        scale = MonitorScale(Cursor.Position);
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int w = (int)Math.Ceiling(W * scale), h = (int)Math.Ceiling(logicalH * scale);
        int x = Math.Max(wa.Left + 4, Math.Min(Cursor.Position.X - w / 2, wa.Right - w - 4));
        int y = Cursor.Position.Y < wa.Top + wa.Height / 2 ? wa.Top + 4 : wa.Bottom - h - 4;
        Bounds = new Rectangle(x, y, w, h);
        hover = -1;
        dirty = true;
        clock.Restart();
        if (!IsHandleCreated) CreateHandle();
        Frame();                 // เตรียมภาพไว้ก่อน (alpha 0) จะได้ไม่มีจังหวะว่างตอนโชว์
        Show();
        SetForegroundWindow(Handle);
        Activate();
        timer.Interval = 15;
        timer.Start();
    }

    float MonitorScale(Point p)
    {
        try
        {
            uint dx, dy;
            if (GetDpiForMonitor(MonitorFromPoint(new POINT { x = p.X, y = p.Y }, 2), 0, out dx, out dy) == 0)
                return dx / 96f;
        }
        catch { }
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
    }

    void StartCapture()
    {
        capturing = true;
        captureError = false;
        captureText = "กดปุ่มลัดใหม่…";
        Hook.SuspendHotkey = true; // ไม่งั้น hook จะกลืนปุ่มลัดเดิมไปก่อน
    }

    void EndCapture()
    {
        capturing = false;
        Hook.SuspendHotkey = false;
    }

    // รับปุ่มทุกตัว (รวม Tab/ลูกศร/Alt) ระหว่างตั้งปุ่มลัด
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!capturing) return base.ProcessCmdKey(ref msg, keyData);
        Keys key = keyData & Keys.KeyCode;
        int mods = ((keyData & Keys.Control) != 0 ? Settings.ModCtrl : 0)
                 | ((keyData & Keys.Shift) != 0 ? Settings.ModShift : 0)
                 | ((keyData & Keys.Alt) != 0 ? Settings.ModAlt : 0);
        if (key == Keys.Escape && mods == 0) { EndCapture(); Redraw(); return true; }
        if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin)
        {
            captureText = Settings.Describe(mods, Keys.None) + " + …";
            captureError = false;
        }
        else if (Settings.IsValidHotkey(mods, key))
        {
            var st = app.CurrentSettings;
            st.HotKey = key;
            st.HotMods = mods;
            EndCapture();
            app.ApplySettings();
        }
        else
        {
            captureText = "ต้องมี Ctrl หรือ Alt ด้วย (หรือใช้ F1–F12)";
            captureError = true;
        }
        Redraw();
        return true;
    }

    void HideNow()
    {
        if (capturing) EndCapture();
        timer.Stop();
        Hide();
        hiddenAt = DateTime.Now;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible) HideNow();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && !capturing) HideNow();
        base.OnKeyDown(e);
    }

    // ---- เมาส์ ----
    int HitAt(Point p)
    {
        var lp = new PointF(p.X / scale, p.Y / scale);
        for (int i = hits.Count - 1; i >= 0; i--)
            if (hits[i].Key.Contains(lp)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitAt(e.Location);
        if (h != hover)
        {
            hover = h;
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            Redraw();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (hover != -1) { hover = -1; Redraw(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            int h = HitAt(e.Location);
            if (h >= 0)
            {
                hits[h].Value();
                if (h < contentHits) Redraw(); else if (Visible) Frame();
            }
        }
        base.OnMouseUp(e);
    }

    // ---- วาด ----
    Font F(float pt, FontStyle style = FontStyle.Regular)
    {
        string key = pt + "|" + style;
        Font f;
        if (!fonts.TryGetValue(key, out f))
        {
            f = new Font("Leelawadee UI", pt * 96f / 72f, style, GraphicsUnit.Pixel);
            fonts[key] = f;
        }
        return f;
    }

    internal static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    internal static void Fill(Graphics g, Color c, RectangleF r, float rad)
    {
        using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
    }

    static void DrawText(Graphics g, string s, Font f, Color c, float x, float y)
    {
        using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y);
    }

    static void TextRight(Graphics g, string s, Font f, Color c, float right, float y)
    {
        float w = g.MeasureString(s, f).Width;
        DrawText(g, s, f, c, right - w, y);
    }

    static void Line(Graphics g, float x1, float x2, float y)
    {
        using (var p = new Pen(Divider)) g.DrawLine(p, x1, y, x2, y);
    }

    bool Hit(RectangleF r, Action a)
    {
        bool hovered = hits.Count == hover;
        hits.Add(new KeyValuePair<RectangleF, Action>(r, a));
        return hovered;
    }

    int MeasureHeight()
    {
        using (var bmp = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            return (int)Math.Ceiling(PaintAll(g, 0)) + 8;
        }
    }

    // มีอะไรเปลี่ยนในการ์ด (hover, สวิตช์, ปุ่มลัด) -> วาดการ์ดใหม่
    void Redraw()
    {
        dirty = true;
        if (Visible) Frame();
    }

    static string Key(bool sleeping, double ms, double bubbleUntil)
    {
        if (sleeping) return "z" + (long)(ms / 80) % 24;
        return ((long)(ms / 480) % 2) + "|" + (ms % 3600 > 3440) + "|" + (ms < bubbleUntil);
    }

    void Frame()
    {
        if (!IsHandleCreated) return;
        nowMs = clock.Elapsed.TotalMilliseconds;
        double t = Math.Min(1.0, nowMs / AppearMs);
        appear = (float)(1 - Math.Pow(1 - t, 3)); // ease-out

        if (dirty || cardBmp == null)
        {
            if (cardBmp != null) cardBmp.Dispose();
            hits.Clear();
            cardBmp = BuildCard(null);
            contentHits = hits.Count;
            dirty = false;
            overlayKey = null;
        }
        string key = Key(!app.CurrentSettings.Enabled, nowMs, bubbleUntilMs);
        if (key != overlayKey)
        {
            hits.RemoveRange(contentHits, hits.Count - contentHits);
            using (var frame = new Bitmap(cardBmp))
            {
                using (var g = Graphics.FromImage(frame)) PaintOverlay(g);
                surface.SetImage(frame);
            }
            overlayKey = key;
        }

        byte alpha = (byte)(255 * appear);
        int dy = (int)Math.Round((1 - appear) * 10 * scale);
        surface.Push(Handle, Left, Top + dy, alpha); // ส่งภาพใหม่เฉพาะเมื่อเปลี่ยน ไม่งั้นแค่ขยับ/จางเข้า
        lastAlpha = alpha;
        lastDy = dy;
        int want = appear < 1 ? 15 : 50;
        if (timer.Interval != want) timer.Interval = want;
    }

    void PaintOverlay(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.ScaleTransform(scale, scale);
        PaintAll(g, 2);
    }

    // การ์ดวาดบนบิตแมปทึบก่อน ตัวหนังสือจึงใช้ ClearType ได้ (คมเหมือนหน้าต่างปกติ)
    // แล้วค่อยตัดเป็นมุมโค้งลงบนบิตแมปโปร่งใส ส่วนแมววาดทับทีหลังใน PaintOverlay
    Bitmap BuildCard(Color? backdrop)
    {
        int w = (int)Math.Ceiling(W * scale), h = (int)Math.Ceiling(logicalH * scale);
        float cardH = logicalH - 8 - CardY;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var content = new Bitmap(w, h, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(content))
            {
                g.Clear(CardBg);
                g.ScaleTransform(scale, scale);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                PaintAll(g, 1);
            }
            using (var g = Graphics.FromImage(bmp))
            {
                if (backdrop.HasValue) g.Clear(backdrop.Value);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(scale, scale);
                for (int i = 6; i >= 1; i--) // เงานุ่มๆ
                    Fill(g, Color.FromArgb(10, 0, 0, 0), new RectangleF(CardX - i, CardY - i + 3, CardW + i * 2, cardH + i * 2), 14 + i);

                g.ResetTransform(); // วางการ์ดแบบ 1:1 ไม่ให้ภาพถูกยืด
                var dev = new RectangleF(CardX * scale, CardY * scale, CardW * scale, cardH * scale);
                using (var p = Round(dev, 14 * scale))
                using (var tb = new TextureBrush(content))
                    g.FillPath(tb, p);

                g.ScaleTransform(scale, scale);
                using (var p = Round(new RectangleF(CardX + 0.5f, CardY + 0.5f, CardW - 1, cardH - 1), 14))
                using (var pen = new Pen(Border)) g.DrawPath(pen, p);
            }
        }
        return bmp;
    }

    // วัดเวลา: PimPid.exe --bench out.txt scale
    public string Bench(float sc, int n)
    {
        scale = sc;
        BuildCard(null).Dispose();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < n; i++) { hits.Clear(); BuildCard(null).Dispose(); }
        double full = sw.Elapsed.TotalMilliseconds / n;
        using (var card = BuildCard(null))
        {
            sw.Restart();
            for (int i = 0; i < n; i++)
                using (var frame = new Bitmap(card))
                {
                    using (var g = Graphics.FromImage(frame)) PaintOverlay(g);
                    surface.SetImage(frame);
                }
        }
        double overlay = sw.Elapsed.TotalMilliseconds / n;
        return "scale " + sc + ": redraw card " + full.ToString("0.0") + " ms, cat-only frame "
            + overlay.ToString("0.0") + " ms, fade frame ~0 ms (no redraw)";
    }

    // ใช้ตรวจหน้าตา: PimPid.exe --preview out.png on|off|bubble [scale]
    public void SavePreview(string path, int fakeTick, float scaleOverride)
    {
        if (scaleOverride > 0) scale = scaleOverride;
        nowMs = 100;
        if (fakeTick == 5) bubbleUntilMs = 1e9;
        hits.Clear();
        using (var bmp = BuildCard(Color.FromArgb(20, 20, 20)))
        {
            using (var g = Graphics.FromImage(bmp)) PaintOverlay(g);
            bmp.Save(path, ImageFormat.Png);
        }
    }

    // วาดแมวให้ 1 พิกเซลแมว = จำนวนพิกเซลจอเต็มๆ (ไม่เบลอ)
    void DrawMascotCrisp(Graphics g, float lx, float ly, float lpx, bool eyesClosed, bool gray)
    {
        var saved = g.Transform;
        g.ResetTransform();
        int px = Math.Max(1, (int)Math.Round(lpx * scale));
        float half = Mascot.Size * px / 2f;
        float x = (float)Math.Round((lx + Mascot.Size * lpx / 2) * scale - half);
        float y = (float)Math.Round((ly + Mascot.Size * lpx / 2) * scale - half);
        Mascot.Draw(g, x, y, px, eyesClosed, gray);
        g.Transform = saved;
        saved.Dispose();
    }

    // วาดทั้งหมด คืนค่าขอบล่างของการ์ด
    // layer 0 = วัดความสูง, 1 = เนื้อหาในการ์ด, 2 = แมวที่โผล่พ้นการ์ด
    float PaintAll(Graphics g, int layer)
    {
        bool draw = layer == 1, overlay = layer == 2;
        Settings st = app.CurrentSettings;
        float cx = CardX + 18, cr = CardX + CardW - 18, y = CardY + 18;

        // --- หัว ---
        if (draw)
        {
            Fill(g, Color.FromArgb(60, 232, 116, 59), new RectangleF(cx, y, 30, 30), 8);
            DrawMascotCrisp(g, cx + 3, y + 3, 1.5f, false, !st.Enabled);
            DrawText(g, "PimPid", F(13, FontStyle.Bold), Text1, cx + 40, y + 2);
            float tw = g.MeasureString("PimPid", F(13, FontStyle.Bold)).Width;
            string badge = st.Enabled ? "ทำงานอยู่" : "พักอยู่";
            float bw = g.MeasureString(badge, F(8.5f)).Width + 14;
            var br = new RectangleF(cx + 40 + tw + 6, y + 6, bw, 20);
            Fill(g, st.Enabled ? Color.FromArgb(40, 34, 197, 94) : Color.FromArgb(45, 156, 163, 175), br, 10);
            DrawText(g, badge, F(8.5f), st.Enabled ? Color.FromArgb(74, 222, 128) : Text2, br.X + 7, br.Y + 2);

            var close = new RectangleF(cr - 22, y + 3, 24, 24);
            if (Hit(close, HideNow)) Fill(g, HoverBg, close, 6);
            using (var pen = new Pen(Text2, 1.6f))
            {
                g.DrawLine(pen, close.X + 7, close.Y + 7, close.Right - 7, close.Bottom - 7);
                g.DrawLine(pen, close.Right - 7, close.Y + 7, close.X + 7, close.Bottom - 7);
            }
        }
        y += 48;

        // --- ปุ่มลัด (คลิกแล้วกดปุ่มใหม่ได้เลย) ---
        if (draw)
        {
            DrawText(g, "ปุ่มลัด", F(10.5f), Text1, cx, y + 2);
            var kf = F(8.5f, FontStyle.Bold);
            if (capturing)
            {
                float w = g.MeasureString(captureText, kf).Width + 20;
                var r = new RectangleF(cr - w, y, w, 24);
                Hit(r, delegate { EndCapture(); });
                Fill(g, Color.FromArgb(60, 232, 116, 59), r, 6);
                using (var path = Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), 6))
                using (var pen = new Pen(captureError ? Color.FromArgb(248, 113, 113) : Accent)) g.DrawPath(pen, path);
                DrawText(g, captureText, kf, captureError ? Color.FromArgb(248, 113, 113) : Text1, r.X + 10, r.Y + 3);
            }
            else
            {
                string[] keys = st.HotkeyText().Split(new[] { " + " }, StringSplitOptions.None);
                var widths = new float[keys.Length];
                float total = 0;
                for (int i = 0; i < keys.Length; i++) { widths[i] = g.MeasureString(keys[i], kf).Width + 14; total += widths[i] + 4; }
                float kx = cr - total + 4;
                var area = new RectangleF(kx - 6, y - 3, total + 6, 30);
                if (Hit(area, StartCapture)) Fill(g, HoverBg, area, 8);
                for (int i = 0; i < keys.Length; i++)
                {
                    Fill(g, Color.FromArgb(14, 14, 18), new RectangleF(kx, y + 2, widths[i], 24), 6);
                    Fill(g, Color.FromArgb(48, 48, 56), new RectangleF(kx, y, widths[i], 23), 6);
                    DrawText(g, keys[i], kf, Text1, kx + 7, y + 3);
                    kx += widths[i] + 4;
                }
            }
        }
        y += 30;
        if (draw)
            DrawText(g, capturing ? "กด Esc เพื่อยกเลิก" : "คลุมดำข้อความ หรือพิมพ์เสร็จแล้วกดได้เลย · คลิกปุ่มเพื่อเปลี่ยน",
                F(8.5f), Text2, cx, y);
        y += 24;
        if (draw) Line(g, cx, cr, y);
        y += 8;

        // --- สวิตช์ ---
        y = Toggle(g, draw, "เปิดใช้งาน", st.Enabled, y, cx, cr,
            delegate { st.Enabled = !st.Enabled; app.ApplySettings(); });
        y = Toggle(g, draw, "แก้คำที่เพิ่งพิมพ์ (ไม่ต้องคลุมดำ)", st.FixTyped, y, cx, cr,
            delegate { st.FixTyped = !st.FixTyped; app.ApplySettings(); });

        // ขอบเขตการแก้ (ใช้เมื่อเปิดสวิตช์ด้านบน)
        if (draw)
        {
            var seg = new RectangleF(cx, y + 2, cr - cx, 30);
            Fill(g, Color.FromArgb(38, 38, 45), seg, 8);
            string[] opts = { "คำล่าสุด", "ทั้งประโยค" };
            for (int i = 0; i < 2; i++)
            {
                var r = new RectangleF(seg.X + 3 + i * (seg.Width - 6) / 2, seg.Y + 3, (seg.Width - 6) / 2, 24);
                bool sel = st.Scope == i;
                int idx = i;
                bool hov = st.FixTyped && Hit(r, delegate { st.Scope = idx; app.ApplySettings(); });
                if (sel) Fill(g, st.FixTyped ? Color.FromArgb(64, 64, 74) : Color.FromArgb(48, 48, 56), r, 6);
                else if (hov) Fill(g, HoverBg, r, 6);
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (var b = new SolidBrush(!st.FixTyped ? Color.FromArgb(90, 90, 100) : sel ? Text1 : Text2))
                    g.DrawString(opts[i], F(9f, sel ? FontStyle.Bold : FontStyle.Regular), b, r, sf);
            }
            DrawText(g, st.Scope == 0 ? "แก้ทีละคำ กดซ้ำเพื่อแก้คำก่อนหน้าเพิ่ม" : "แก้ทุกคำที่พิมพ์ต่อกันด้วยภาษาเดียวกัน",
                F(8.5f), st.FixTyped ? Text2 : Color.FromArgb(90, 90, 100), cx, y + 38);
        }
        y += 64;

        y = Toggle(g, draw, "สลับภาษาคีย์บอร์ดให้หลังแก้", st.SwitchLayout, y, cx, cr,
            delegate { st.SwitchLayout = !st.SwitchLayout; app.ApplySettings(); });
        y = Toggle(g, draw, "เปิดพร้อม Windows", MainForm.StartupEnabled(), y, cx, cr,
            delegate { MainForm.SetStartup(!MainForm.StartupEnabled()); });
        y += 8;
        if (draw) Line(g, cx, cr, y);
        y += 14;

        // --- สถิติวันนี้ ---
        int toEn = st.TodayToEn(), toTh = st.TodayToTh(), sum = toEn + toTh;
        if (draw)
        {
            DrawText(g, "วันนี้ช่วยแก้ไป", F(10.5f, FontStyle.Bold), Text1, cx, y);
            TextRight(g, sum + " ครั้ง", F(10.5f), Text1, cr, y);
        }
        y += 28;
        if (draw)
        {
            var bar = new RectangleF(cx, y, cr - cx, 6);
            Fill(g, SwitchOff, bar, 3);
            if (sum > 0)
            {
                Fill(g, Accent, bar, 3);
                if (toEn > 0) Fill(g, Blue, new RectangleF(bar.X, bar.Y, Math.Max(6, bar.Width * toEn / sum), 6), 3);
            }
        }
        y += 14;
        if (draw)
        {
            float lx = cx;
            Fill(g, Blue, new RectangleF(lx, y + 6, 7, 7), 3.5f);
            string a = "แก้เป็น Eng  " + toEn;
            DrawText(g, a, F(8.5f), Text2, lx + 11, y);
            lx += 11 + g.MeasureString(a, F(8.5f)).Width + 14;
            Fill(g, Accent, new RectangleF(lx, y + 6, 7, 7), 3.5f);
            DrawText(g, "แก้เป็นไทย  " + toTh, F(8.5f), Text2, lx + 11, y);
        }
        y += 22;
        if (draw)
        {
            string last = app.LastFrom != null ? "ล่าสุด  " + app.LastFrom + "  →  " + app.LastTo
                : sum == 0 ? "ยังไม่ได้แก้อะไรเลย ลองพิมพ์ผิดภาษาแล้วกดดูสิ"
                : "ล่าสุด  —";
            using (var b = new SolidBrush(Text2))
            using (var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter })
                g.DrawString(last, F(8.5f), b, new RectangleF(cx, y, cr - cx, 20), sf);
        }
        y += 26;
        if (draw) Line(g, cx, cr, y);
        y += 14;

        // --- ท้าย ---
        if (draw)
        {
            DrawText(g, "ไม่บันทึกสิ่งที่คุณพิมพ์ลงเครื่อง", F(8.5f), Text2, cx, y + 4);
            Pill(g, "ปิดโปรแกรม", cr, y, app.Quit);
        }
        y += 26 + 16;

        // --- มาสคอต (วาดทีหลังสุดให้ทับขอบการ์ด) ---
        if (overlay)
        {
            float px = 4, mx = CardX + CardW - 64 - 64, bob = (long)(nowMs / 480) % 2 == 0 ? 0 : 1.5f;
            float my = CardY + 24 - 64 + bob;
            bool sleeping = !st.Enabled;
            bool blink = sleeping || nowMs % 3600 > 3440;
            Hit(new RectangleF(mx, my, 64, 64), delegate { bubbleUntilMs = clock.Elapsed.TotalMilliseconds + 1800; });
            DrawMascotCrisp(g, mx, my, px, blink, sleeping);
            if (sleeping)
            {
                float zy = ((long)(nowMs / 80) % 24) / 3f;
                DrawText(g, "z", F(9, FontStyle.Bold), Text2, mx + 62, my + 14 - zy);
                DrawText(g, "Z", F(11, FontStyle.Bold), Text2, mx + 70, my + 2 - zy);
            }
            else if (nowMs < bubbleUntilMs)
            {
                var bub = new RectangleF(mx - 92, my + 12, 84, 26);
                Fill(g, Color.White, bub, 13);
                using (var tri = new GraphicsPath())
                {
                    tri.AddPolygon(new[] { new PointF(bub.Right - 2, bub.Y + 9), new PointF(bub.Right + 7, bub.Y + 13), new PointF(bub.Right - 2, bub.Y + 17) });
                    g.FillPath(Brushes.White, tri);
                }
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (var b = new SolidBrush(Color.FromArgb(59, 36, 23)))
                    g.DrawString("เมี้ยว~", F(9, FontStyle.Bold), b, bub, sf);
            }
        }
        return y;
    }

    float Toggle(Graphics g, bool draw, string label, bool on, float y, float cx, float cr, Action toggle)
    {
        if (draw)
        {
            var row = new RectangleF(cx - 8, y, cr - cx + 16, 36);
            if (Hit(row, toggle)) Fill(g, HoverBg, row, 8);
            DrawText(g, label, F(10), Text1, cx, y + 8);
            var sw = new RectangleF(cr - 36, y + 8, 36, 20);
            Fill(g, on ? Accent : SwitchOff, sw, 10);
            float kx = on ? sw.Right - 18 : sw.X + 2;
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, kx, sw.Y + 2, 16, 16);
        }
        return y + 36;
    }

    float Pill(Graphics g, string text, float right, float y, Action a)
    {
        var f = F(8.5f);
        float w = g.MeasureString(text, f).Width + 22;
        var r = new RectangleF(right - w, y, w, 26);
        bool hov = Hit(r, a);
        Fill(g, hov ? Color.FromArgb(55, 55, 64) : Color.FromArgb(40, 40, 47), r, 8);
        using (var p = Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), 8))
        using (var pen = new Pen(Border)) g.DrawPath(pen, p);
        DrawText(g, text, f, Text1, r.X + 11, r.Y + 4);
        return r.X;
    }
}
