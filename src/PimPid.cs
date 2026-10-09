using System;
using System.Reflection;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("PimPid")]
[assembly: AssemblyProduct("PimPid")]
[assembly: AssemblyDescription("แก้ข้อความที่พิมพ์ผิดภาษา ไทย/อังกฤษ")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

static class Program
{
    // true = Windows เปิดให้ตอนเปิดเครื่อง -> ไม่ต้องเด้งการ์ดแจ้งเตือน
    public static bool Silent;

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

    [STAThread]
    static void Main(string[] args)
    {
        // Per-monitor DPI v2: คมทุกจอ แม้แต่ละจอซูมไม่เท่ากัน
        try { if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware(); }
        catch (EntryPointNotFoundException) { SetProcessDPIAware(); }

        if (args.Length == 3 && args[0] == "--bench")
        {
            var p = new PopupForm(new MainForm());
            float sc = float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
            System.IO.File.WriteAllText(args[1], p.Bench(sc, 30));
            return;
        }
        if (args.Length >= 3 && args[0] == "--preview")
        {
            var app = new MainForm();
            if (args[2] == "off") app.CurrentSettings.Enabled = false;
            app.LastFrom = "l;ylfu8iy[";
            app.LastTo = "สวัสดีครับ";
            new PopupForm(app).SavePreview(args[1], args[2] == "bubble" ? 5 : 3,
                args.Length > 3 ? float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0);
            return;
        }
        Silent = Array.IndexOf(args, "--startup") >= 0;
        bool isNew;
        using (var mutex = new Mutex(true, "PimPid_SingleInstance", out isNew))
        {
            if (!isNew)
            {
                MessageBox.Show("PimPid กำลังทำงานอยู่แล้ว (ดูที่ไอคอนถาดระบบมุมขวาล่าง)", "PimPid");
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}

class MainForm : Form
{
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetKeyboardLayoutList(int n, IntPtr[] list);

    const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    Settings settings;
    readonly NotifyIcon tray;
    readonly Icon iconOn, iconOff;
    PopupForm popup;
    bool busy;

    // สำหรับ "กดซ้ำเพื่อแก้คำก่อนหน้าเพิ่ม"
    int lastFixVersion = -1, lastFixStart;
    DateTime lastFixTime;

    // ข้อความที่แก้ล่าสุด (อยู่ในหน่วยความจำเท่านั้น ไว้โชว์ในป๊อปอัป)
    public string LastFrom, LastTo;
    public Settings CurrentSettings { get { return settings; } }

    public MainForm()
    {
        ShowInTaskbar = false;
        settings = Settings.Load();
        MigrateStartup();
        int iconSize = SystemInformation.SmallIconSize.Width;
        iconOn = Mascot.MakeIcon(false, iconSize);
        iconOff = Mascot.MakeIcon(true, iconSize);

        tray = new NotifyIcon();
        tray.MouseUp += delegate { if (popup != null) popup.Toggle(); };
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated)
        {
            CreateHandle();
            tray.Visible = true;
            popup = new PopupForm(this);
            Hook.OnHotkey = delegate { BeginInvoke((MethodInvoker)OnHotkey); };
            ApplySettings();
            Hook.Start();
            if (!Program.Silent)
                ToastForm.Pop(settings.HotkeyText(), delegate { popup.Toggle(); });
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        tray.Visible = false;
        tray.Dispose();
        base.OnFormClosed(e);
        Application.Exit();
    }

    public void Quit() { Close(); }

    // ---- ตั้งค่า ----
    public void ApplySettings()
    {
        Hook.HotVk = (int)settings.HotKey;
        Hook.HotMods = settings.HotMods;
        Hook.HotkeyEnabled = settings.Enabled;
        Hook.Tracking = settings.Enabled && settings.FixTyped;
        if (!Hook.Tracking) Hook.Clear();
        SaveSettings();
        tray.Icon = settings.Enabled ? iconOn : iconOff;
        tray.Text = settings.Enabled ? "PimPid — " + settings.HotkeyText() : "PimPid — พักอยู่";
    }

    void SaveSettings()
    {
        try { settings.Save(); } catch { }
    }

    void RecordFix(string from, string to, bool toEnglish)
    {
        LastFrom = from.Trim();
        LastTo = to.Trim();
        settings.CountFix(toEnglish);
        SaveSettings();
    }

    // ---- เมื่อกดปุ่มลัด ----
    void OnHotkey()
    {
        if (busy) return;
        busy = true;
        try
        {
            WaitModifiersReleased(); // ถ้ายังกด Ctrl ค้าง Backspace จะกลายเป็นลบทั้งคำ
            var snap = Hook.Take();
            if (settings.FixTyped && snap.Text.Trim().Length > 0) FixTyped(snap);
            else FixSelection();
        }
        catch (Exception ex) { tray.ShowBalloonTip(3000, "PimPid error", ex.Message, ToolTipIcon.Error); }
        finally { busy = false; }
    }

    static int WordStart(string t, int pos)
    {
        int i = pos;
        while (i > 0 && t[i - 1] == ' ') i--;
        while (i > 0 && t[i - 1] != ' ') i--;
        return i;
    }

    // แก้ข้อความที่เพิ่งพิมพ์: ลบด้วย Backspace แล้วพิมพ์ใหม่เป็นอีกภาษา
    void FixTyped(Hook.Snapshot snap)
    {
        string t = snap.Text;
        int count = t.Length;
        bool extend = settings.Scope == 0 && snap.Version == lastFixVersion
            && (DateTime.Now - lastFixTime).TotalSeconds < 4 && lastFixStart > 0 && lastFixStart <= count;
        int convEnd = extend ? lastFixStart : count;
        int start;

        if (settings.Scope == 1 && !extend)
        {
            // ย้อนกลับไปจนเจอตัวอักษรที่พิมพ์ด้วยภาษาอื่น
            start = convEnd;
            int lang = -1;
            while (start > 0)
            {
                if (t[start - 1] != ' ')
                {
                    if (lang == -1) lang = snap.Langs[start - 1];
                    else if (snap.Langs[start - 1] != lang) break;
                }
                start--;
            }
            while (start < convEnd && t[start] == ' ') start++;
        }
        else start = WordStart(t, convEnd);

        if (start >= convEnd) return;
        string seg = t.Substring(start, convEnd - start), tail = t.Substring(convEnd);

        int segLang = -1;
        for (int i = convEnd - 1; i >= start; i--)
            if (t[i] != ' ') { segLang = snap.Langs[i]; break; }
        if (segLang == -1) return;

        bool toEnglish = segLang == Hook.LangThai;
        string replacement = Converter.Convert(seg, toEnglish) + tail;

        Input.Backspace(count - start);
        Input.Type(replacement);

        RecordFix(seg, Converter.Convert(seg, toEnglish), toEnglish);
        lastFixVersion = Hook.ReplaceFrom(start, replacement, toEnglish ? Hook.LangEnglish : Hook.LangThai, snap.Version);
        lastFixStart = start;
        lastFixTime = DateTime.Now;

        if (settings.SwitchLayout) SwitchLayout(GetForegroundWindow(), toEnglish);
    }

    // แก้ข้อความที่คลุมดำไว้ ผ่านคลิปบอร์ด
    void FixSelection()
    {
        IntPtr target = GetForegroundWindow();
        DataObject backup = BackupClipboard();
        uint seq = GetClipboardSequenceNumber();

        Input.Combo(Keys.ControlKey, Keys.C);
        if (!WaitClipboardChange(seq, 600))
            return; // ไม่มีข้อความถูกคลุมไว้

        string text = null;
        for (int i = 0; i < 10 && text == null; i++)
        {
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); else break; }
            catch (ExternalException) { Thread.Sleep(30); }
        }
        if (string.IsNullOrEmpty(text)) { RestoreClipboard(backup); return; }

        bool toEnglish;
        string fixedText = Converter.Convert(text, out toEnglish);

        SetClipboardText(fixedText);
        Input.Combo(Keys.ControlKey, Keys.V);
        Thread.Sleep(250); // ให้โปรแกรมปลายทางวางเสร็จก่อนคืนคลิปบอร์ด
        RestoreClipboard(backup);
        Hook.Clear();
        RecordFix(text, fixedText, toEnglish);

        if (settings.SwitchLayout) SwitchLayout(target, toEnglish);
    }

    static void WaitModifiersReleased()
    {
        int[] mods = { 0x10, 0x11, 0x12, 0x5B, 0x5C };
        for (int t = 0; t < 150; t++) // สูงสุด ~1.5 วินาที
        {
            bool down = false;
            foreach (int k in mods) if ((GetAsyncKeyState(k) & 0x8000) != 0) down = true;
            if (!down) return;
            Thread.Sleep(10);
        }
        Input.ReleaseModifiers();
    }

    static bool WaitClipboardChange(uint seq, int timeoutMs)
    {
        for (int t = 0; t < timeoutMs; t += 15)
        {
            if (GetClipboardSequenceNumber() != seq) { Thread.Sleep(30); return true; }
            Thread.Sleep(15);
        }
        return false;
    }

    static DataObject BackupClipboard()
    {
        try
        {
            IDataObject src = Clipboard.GetDataObject();
            if (src == null) return null;
            var copy = new DataObject();
            foreach (string f in src.GetFormats(false))
            {
                try { object d = src.GetData(f, false); if (d != null) copy.SetData(f, false, d); }
                catch { }
            }
            return copy;
        }
        catch { return null; }
    }

    static void RestoreClipboard(DataObject backup)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (backup == null || backup.GetFormats().Length == 0) Clipboard.Clear();
                else Clipboard.SetDataObject(backup, true);
                return;
            }
            catch (ExternalException) { Thread.Sleep(30); }
        }
    }

    static void SetClipboardText(string s)
    {
        for (int i = 0; i < 10; i++)
        {
            try { Clipboard.SetText(s); return; }
            catch (ExternalException) { Thread.Sleep(30); }
        }
    }

    // เปลี่ยนภาษาคีย์บอร์ดให้ตรงกับข้อความที่แปลงแล้ว
    static void SwitchLayout(IntPtr hwnd, bool toEnglish)
    {
        var list = new IntPtr[32];
        int n = GetKeyboardLayoutList(list.Length, list);
        for (int i = 0; i < n; i++)
        {
            int lang = (int)((long)list[i] & 0xFFFF);
            bool match = toEnglish ? (lang & 0x3FF) == 0x09 : lang == Hook.LangThai;
            if (match)
            {
                PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, list[i]);
                return;
            }
        }
    }

    // ---- เปิดอัตโนมัติตอน login ----
    // ถ้าเคยตั้ง "เปิดพร้อม Windows" ไว้ตอนยังชื่อ LangFix ให้ย้ายมาเป็นชื่อใหม่
    public static void MigrateStartup()
    {
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (k == null || k.GetValue("LangFix") == null) return;
            k.DeleteValue("LangFix", false);
            SetStartup(true);
        }
    }

    public static bool StartupEnabled()
    {
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue("PimPid") != null;
    }

    public static void SetStartup(bool on)
    {
        using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue("PimPid", "\"" + Application.ExecutablePath + "\" --startup");
            else k.DeleteValue("PimPid", false);
        }
    }
}

// ส่งการกดปุ่มเข้าโปรแกรมที่กำลังใช้อยู่
static class Input
{
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

    const uint KEYUP = 2, UNICODE = 4;

    static INPUT Key(ushort vk, ushort scan, uint flags)
    {
        var i = new INPUT { type = 1 };
        i.u.ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags };
        return i;
    }

    static void Send(INPUT[] arr)
    {
        if (arr.Length > 0) SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void Combo(Keys mod, Keys key)
    {
        Send(new[]
        {
            Key((ushort)mod, 0, 0), Key((ushort)key, 0, 0),
            Key((ushort)key, 0, KEYUP), Key((ushort)mod, 0, KEYUP)
        });
    }

    public static void Backspace(int n)
    {
        var arr = new INPUT[n * 2];
        for (int i = 0; i < n; i++)
        {
            arr[i * 2] = Key(0x08, 0, 0);
            arr[i * 2 + 1] = Key(0x08, 0, KEYUP);
        }
        Send(arr);
    }

    public static void Type(string s)
    {
        var arr = new INPUT[s.Length * 2];
        for (int i = 0; i < s.Length; i++)
        {
            arr[i * 2] = Key(0, s[i], UNICODE);
            arr[i * 2 + 1] = Key(0, s[i], UNICODE | KEYUP);
        }
        Send(arr);
    }

    public static void ReleaseModifiers()
    {
        ushort[] mods = { 0x10, 0x11, 0x12, 0x5B, 0x5C };
        var arr = new INPUT[mods.Length];
        for (int i = 0; i < mods.Length; i++) arr[i] = Key(mods[i], 0, KEYUP);
        Send(arr);
    }
}
