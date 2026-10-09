using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// การ์ดแจ้งเตือนเล็กๆ ตอนเปิดโปรแกรม (แทนแจ้งเตือนของ Windows) ไม่แย่งโฟกัส
class ToastForm : Form
{
    const int W = 316, H = 92, M = 8;           // ขนาดการ์ด + ขอบเผื่อเงา (logical px)
    const int ShowMs = 4000, FadeMs = 220;

    readonly string hotkey;
    readonly Action onClick;
    readonly float scale;
    readonly Timer timer = new Timer { Interval = 15 };
    readonly LayeredSurface surface = new LayeredSurface();
    int lastBob = -1;
    readonly DateTime start = DateTime.Now;
    bool closing;
    DateTime closeAt;

    public static void Pop(string hotkeyText, Action onClick)
    {
        new ToastForm(hotkeyText, onClick).Show();
    }

    ToastForm(string hotkeyText, Action onClick)
    {
        hotkey = hotkeyText;
        this.onClick = onClick;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
        var wa = Screen.PrimaryScreen.WorkingArea;
        int w = (int)Math.Ceiling((W + M * 2) * scale), h = (int)Math.Ceiling((H + M * 2) * scale);
        Bounds = new Rectangle(wa.Right - w - (int)(6 * scale), wa.Bottom - h - (int)(6 * scale), w, h);
        Cursor = Cursors.Hand;
        timer.Tick += Tick;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x8 | 0x08000000; // LAYERED | TOOLWINDOW | TOPMOST | NOACTIVATE
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Render(0);
        timer.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Dismiss();
        if (onClick != null) onClick();
    }

    void Dismiss()
    {
        if (closing) return;
        closing = true;
        closeAt = DateTime.Now;
    }

    void Tick(object sender, EventArgs e)
    {
        double t = (DateTime.Now - start).TotalMilliseconds;
        if (!closing && t > ShowMs) Dismiss();
        float a;
        if (closing)
        {
            a = 1f - (float)((DateTime.Now - closeAt).TotalMilliseconds / FadeMs);
            if (a <= 0) { timer.Stop(); surface.Dispose(); Close(); return; }
        }
        else a = Math.Min(1f, (float)(t / FadeMs));
        Render(a);
    }

    void Render(float alpha)
    {
        alpha = Math.Max(0f, Math.Min(1f, alpha));
        int bobStep = (int)((DateTime.Now - start).TotalMilliseconds / 450) % 2;
        if (bobStep != lastBob)
        {
            Build(bobStep * 1.5f);
            lastBob = bobStep;
        }
        float e = 1 - (1 - alpha) * (1 - alpha) * (1 - alpha); // ease-out
        surface.Push(Handle, Left, Top + (int)Math.Round((1 - e) * 8 * scale), (byte)(255 * e));
    }

    void Build(float bob)
    {
        int w = Width, h = Height;
        using (var content = new Bitmap(w, h, PixelFormat.Format24bppRgb))
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(content))
            {
                g.Clear(PopupForm.CardBg);
                g.ScaleTransform(scale, scale);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                Paint(g);
            }
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(scale, scale);
                for (int i = 6; i >= 1; i--)
                    PopupForm.Fill(g, Color.FromArgb(12, 0, 0, 0), new RectangleF(M - i, M - i + 3, W + i * 2, H + i * 2), 14 + i);
                g.ResetTransform();
                using (var p = PopupForm.Round(new RectangleF(M * scale, M * scale, W * scale, H * scale), 14 * scale))
                using (var tb = new TextureBrush(content))
                    g.FillPath(tb, p);
                g.ScaleTransform(scale, scale);
                using (var p = PopupForm.Round(new RectangleF(M + 0.5f, M + 0.5f, W - 1, H - 1), 14))
                using (var pen = new Pen(PopupForm.Border)) g.DrawPath(pen, p);

                // แมว: พิกเซลตรงจอ
                g.ResetTransform();
                int px = Math.Max(1, (int)Math.Round(3.5f * scale));
                Mascot.Draw(g, (float)Math.Round((M + 18) * scale), (float)Math.Round((M + H / 2f - 28 + bob) * scale), px, false, false);
            }
            surface.SetImage(bmp);
        }
    }

    new void Paint(Graphics g)
    {
        float x = M + 94, y = M + 18;
        using (var title = new Font("Leelawadee UI", 10.5f * 96 / 72, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var body = new Font("Leelawadee UI", 9f * 96 / 72, GraphicsUnit.Pixel))
        using (var keyF = new Font("Leelawadee UI", 8.5f * 96 / 72, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var t1 = new SolidBrush(PopupForm.Text1))
        using (var t2 = new SolidBrush(PopupForm.Text2))
        {
            g.DrawString("PimPid พร้อมแล้ว!", title, t1, x, y);
            y += 30;
            string lead = "พิมพ์ผิดภาษา? กด";
            g.DrawString(lead, body, t2, x, y + 1);
            float kx = x + g.MeasureString(lead, body).Width + 4;
            foreach (string k in hotkey.Split(new[] { " + " }, StringSplitOptions.None))
            {
                float kw = g.MeasureString(k, keyF).Width + 12;
                PopupForm.Fill(g, Color.FromArgb(14, 14, 18), new RectangleF(kx, y + 2, kw, 23), 5);
                PopupForm.Fill(g, Color.FromArgb(48, 48, 56), new RectangleF(kx, y, kw, 22), 5);
                g.DrawString(k, keyF, t1, kx + 6, y + 2);
                kx += kw + 4;
            }
        }
    }
}
