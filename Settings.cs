using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

// ค่าที่ผู้ใช้ตั้ง เก็บไว้ที่ %APPDATA%\PimPid\settings.ini
class Settings
{
    public const int ModCtrl = 1, ModShift = 2, ModAlt = 4;

    public bool Enabled = true;
    public Keys HotKey = Keys.Space;
    public int HotMods = ModCtrl | ModShift;
    public bool FixTyped = true;      // ไม่ได้คลุมดำ -> แก้ข้อความที่เพิ่งพิมพ์
    public int Scope = 0;             // 0 = คำล่าสุด (กดซ้ำเพื่อขยาย), 1 = ทั้งช่วงที่พิมพ์ภาษาเดียวกัน
    public bool SwitchLayout = true;  // สลับภาษาคีย์บอร์ดหลังแก้

    // สถิติรายวัน (นับจำนวนครั้ง ไม่เก็บข้อความ)
    public string StatDate = "";
    public int StatToEn, StatToTh;

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PimPid", "settings.ini");

    public Settings Clone() { return (Settings)MemberwiseClone(); }

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            // ย้ายจากชื่อเดิม (LangFix) ครั้งแรกที่เปิดหลังเปลี่ยนชื่อ
            string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LangFix", "settings.ini");
            if (!File.Exists(FilePath) && File.Exists(old))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.Copy(old, FilePath);
                Directory.Delete(Path.GetDirectoryName(old), true);
            }
            if (!File.Exists(FilePath)) return s;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "Enabled": s.Enabled = v == "1"; break;
                    case "HotKey": s.HotKey = (Keys)int.Parse(v); break;
                    case "HotMods": s.HotMods = int.Parse(v); break;
                    case "FixTyped": s.FixTyped = v == "1"; break;
                    case "Scope": s.Scope = int.Parse(v); break;
                    case "SwitchLayout": s.SwitchLayout = v == "1"; break;
                    case "StatDate": s.StatDate = v; break;
                    case "StatToEn": s.StatToEn = int.Parse(v); break;
                    case "StatToTh": s.StatToTh = int.Parse(v); break;
                }
            }
        }
        catch { }
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        File.WriteAllLines(FilePath, new[]
        {
            "Enabled=" + (Enabled ? 1 : 0),
            "HotKey=" + (int)HotKey,
            "HotMods=" + HotMods,
            "FixTyped=" + (FixTyped ? 1 : 0),
            "Scope=" + Scope,
            "SwitchLayout=" + (SwitchLayout ? 1 : 0),
            "StatDate=" + StatDate,
            "StatToEn=" + StatToEn,
            "StatToTh=" + StatToTh,
        });
    }

    static string Today() { return DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture); }
    public int TodayToEn() { return StatDate == Today() ? StatToEn : 0; }
    public int TodayToTh() { return StatDate == Today() ? StatToTh : 0; }

    public void CountFix(bool toEnglish)
    {
        if (StatDate != Today()) { StatDate = Today(); StatToEn = StatToTh = 0; }
        if (toEnglish) StatToEn++; else StatToTh++;
    }

    public string HotkeyText() { return Describe(HotMods, HotKey); }

    public static string Describe(int mods, Keys key)
    {
        var parts = new List<string>();
        if ((mods & ModCtrl) != 0) parts.Add("Ctrl");
        if ((mods & ModShift) != 0) parts.Add("Shift");
        if ((mods & ModAlt) != 0) parts.Add("Alt");
        if (key != Keys.None) parts.Add(KeyName(key));
        return string.Join(" + ", parts.ToArray());
    }

    static string KeyName(Keys k)
    {
        if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
        switch (k)
        {
            case Keys.Oemtilde: return "`";
            case Keys.OemMinus: return "-";
            case Keys.Oemplus: return "=";
            case Keys.OemOpenBrackets: return "[";
            case Keys.OemCloseBrackets: return "]";
            case Keys.OemPipe: return "\\";
            case Keys.OemSemicolon: return ";";
            case Keys.OemQuotes: return "'";
            case Keys.Oemcomma: return ",";
            case Keys.OemPeriod: return ".";
            case Keys.OemQuestion: return "/";
            case Keys.Next: return "PageDown";
            case Keys.Prior: return "PageUp";
            case Keys.Scroll: return "ScrollLock";
        }
        return k.ToString();
    }

    // กันไม่ให้ตั้งปุ่มลัดที่ไปทับการพิมพ์ปกติ
    public static bool IsValidHotkey(int mods, Keys key)
    {
        if (key == Keys.None) return false;
        if ((mods & (ModCtrl | ModAlt)) != 0) return true;
        return (key >= Keys.F1 && key <= Keys.F24) || key == Keys.Pause || key == Keys.Scroll
            || key == Keys.Insert || key == Keys.Apps;
    }
}
