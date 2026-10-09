using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Keyboard/mouse hook บน thread แยก: จับปุ่มลัด และจำตัวอักษรที่เพิ่งพิมพ์ (ในหน่วยความจำเท่านั้น)
static class Hook
{
    delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelProc fn, IntPtr hMod, uint tid);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint tid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, [Out] char[] buf, int cch, uint flags, IntPtr hkl);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    public const int LangThai = 0x041E, LangEnglish = 0x0409;
    const int MaxChars = 300;

    // ตั้งจาก UI thread
    public static volatile bool HotkeyEnabled = true;
    public static volatile bool SuspendHotkey;   // ตอนเปิดหน้าตั้งค่า
    public static volatile bool Tracking = true;
    public static volatile int HotVk, HotMods;
    public static Action OnHotkey;               // ถูกเรียกบน hook thread -> ต้อง BeginInvoke เอง

    static readonly object Sync = new object();
    static readonly List<char> chars = new List<char>();
    static readonly List<int> langs = new List<int>();
    static int version;
    static IntPtr lastFg;
    static int swallowUp = -1;
    static LowLevelProc kbProc, msProc; // กัน GC เก็บ delegate

    public class Snapshot { public string Text; public int[] Langs; public int Version; }

    public static void Start()
    {
        var t = new Thread(Loop) { IsBackground = true, Name = "PimPidHook" };
        t.Start();
    }

    static void Loop()
    {
        kbProc = KbHook;
        msProc = MsHook;
        IntPtr mod = GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName);
        SetWindowsHookEx(13, kbProc, mod, 0); // WH_KEYBOARD_LL
        SetWindowsHookEx(14, msProc, mod, 0); // WH_MOUSE_LL
        Application.Run(); // message loop ของ hook
    }

    static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
    static bool WinDown() { return Down(0x5B) || Down(0x5C); }

    static int CurrentMods()
    {
        int m = 0;
        if (Down(0x11)) m |= Settings.ModCtrl;
        if (Down(0x10)) m |= Settings.ModShift;
        if (Down(0x12)) m |= Settings.ModAlt;
        return m;
    }

    static IntPtr KbHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            int msg = wParam.ToInt32();
            bool down = msg == 0x100 || msg == 0x104, up = msg == 0x101 || msg == 0x105;
            bool injected = (k.flags & 0x10) != 0; // ปุ่มที่โปรแกรมส่งเอง (รวมของเรา) ไม่นับ
            int vk = (int)k.vkCode;

            if (!injected)
            {
                if (down && vk == swallowUp) return (IntPtr)1; // กดค้าง (auto-repeat)
                if (down && HotkeyEnabled && !SuspendHotkey && vk == HotVk
                    && CurrentMods() == HotMods && !WinDown())
                {
                    swallowUp = vk;
                    var cb = OnHotkey;
                    if (cb != null) cb();
                    return (IntPtr)1;
                }
                if (up && vk == swallowUp) { swallowUp = -1; return (IntPtr)1; }
                if (down && Tracking)
                {
                    try { Track(vk, k.scanCode); } catch { }
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    static IntPtr MsHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int m = wParam.ToInt32();
            if (m == 0x201 || m == 0x204 || m == 0x207) Clear(); // คลิก = เคอร์เซอร์อาจย้ายที่
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    static bool IsModifier(int vk)
    {
        return vk == 0x10 || vk == 0x11 || vk == 0x12 || (vk >= 0xA0 && vk <= 0xA5)
            || vk == 0x5B || vk == 0x5C || vk == 0x14 || vk == 0x90 || vk == 0x91;
    }

    static void Track(int vk, uint scan)
    {
        if (IsModifier(vk)) return;
        IntPtr fg = GetForegroundWindow();
        lock (Sync)
        {
            if (fg != lastFg) { ClearLocked(); lastFg = fg; }
            if (Down(0x11) || Down(0x12) || WinDown()) { ClearLocked(); return; } // Ctrl+?, Alt+? ฯลฯ

            if (vk == 0x08) // Backspace
            {
                if (chars.Count > 0) { chars.RemoveAt(chars.Count - 1); langs.RemoveAt(langs.Count - 1); }
                version++;
                return;
            }

            IntPtr hkl = GetKeyboardLayout(GetWindowThreadProcessId(fg, IntPtr.Zero));
            var state = new byte[256];
            if (Down(0x10)) state[0x10] = 0x80;
            if ((GetKeyState(0x14) & 1) != 0) state[0x14] = 1;
            var buf = new char[8];
            int n = ToUnicodeEx((uint)vk, scan, state, buf, buf.Length, 4, hkl); // 4 = ไม่แตะ state ของระบบ

            // ปุ่มที่ไม่ใช่ตัวอักษร (ลูกศร, Enter, Tab, Delete, ...) = เริ่มนับใหม่
            if (n <= 0) { ClearLocked(); return; }
            int lang = (int)((long)hkl & 0xFFFF);
            for (int i = 0; i < n; i++)
            {
                if (buf[i] < 0x20) { ClearLocked(); return; }
                chars.Add(buf[i]);
                langs.Add(lang);
            }
            if (chars.Count > MaxChars)
            {
                chars.RemoveRange(0, chars.Count - MaxChars);
                langs.RemoveRange(0, langs.Count - MaxChars);
            }
            version++;
        }
    }

    public static void Clear() { lock (Sync) ClearLocked(); }

    static void ClearLocked()
    {
        chars.Clear();
        langs.Clear();
        version++;
    }

    public static Snapshot Take()
    {
        lock (Sync)
            return new Snapshot { Text = new string(chars.ToArray()), Langs = langs.ToArray(), Version = version };
    }

    // แทนที่ตั้งแต่ start จนจบด้วยข้อความที่แก้แล้ว คืนค่า version ใหม่
    public static int ReplaceFrom(int start, string text, int lang, int expectedVersion)
    {
        lock (Sync)
        {
            if (version != expectedVersion || start > chars.Count) { ClearLocked(); return version; }
            chars.RemoveRange(start, chars.Count - start);
            langs.RemoveRange(start, langs.Count - start);
            foreach (char c in text) { chars.Add(c); langs.Add(lang); }
            version++;
            return version;
        }
    }
}
