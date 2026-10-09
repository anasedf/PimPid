using System.Collections.Generic;
using System.Text;

// แปลงข้อความที่พิมพ์ผิดภาษา ระหว่างแป้น English (QWERTY) <-> ไทย (เกษมณี)
public static class Converter
{
    // ตำแหน่งปุ่มเดียวกันต้องอยู่ index เดียวกันทั้งสองสตริง
    const string En =
        "`1234567890-=" + "qwertyuiop[]\\" + "asdfghjkl;'" + "zxcvbnm,./" +
        "~!@#$%^&*()_+" + "QWERTYUIOP{}|" + "ASDFGHJKL:\"" + "ZXCVBNM<>?";
    const string Th =
        "_ๅ/-ภถุึคตจขช" + "ๆไำพะัีรนยบลฃ" + "ฟหกดเ้่าสวง" + "ผปแอิืทมใฝ" +
        "%+๑๒๓๔ู฿๕๖๗๘๙" + "๐\"ฎฑธํ๊ณฯญฐ,ฅ" + "ฤฆฏโฌ็๋ษศซ." + "()ฉฮฺ์?ฒฬฦ";

    static readonly Dictionary<char, char> EnToTh = new Dictionary<char, char>();
    static readonly Dictionary<char, char> ThToEn = new Dictionary<char, char>();

    static Converter()
    {
        if (En.Length != Th.Length)
            throw new System.Exception("Key map length mismatch: " + En.Length + " vs " + Th.Length);
        for (int i = 0; i < En.Length; i++)
        {
            EnToTh[En[i]] = Th[i];
            ThToEn[Th[i]] = En[i];
        }
    }

    static bool IsThai(char c) { return c >= '฀' && c <= '๿'; }
    static bool IsLatin(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); }

    // true = ข้อความเป็นไทย (ต้องแปลงเป็นอังกฤษ)
    public static bool LooksThai(string s)
    {
        int thai = 0, latin = 0;
        foreach (char c in s)
        {
            if (IsThai(c)) thai++;
            else if (IsLatin(c)) latin++;
        }
        return thai > latin;
    }

    // เดาทิศทางจากเนื้อหา (ใช้กับข้อความที่คลุมดำ)
    public static string Convert(string s, out bool toEnglish)
    {
        toEnglish = LooksThai(s);
        return Convert(s, toEnglish);
    }

    public static string Convert(string s, bool toEnglish)
    {
        var map = toEnglish ? ThToEn : EnToTh;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            char r;
            sb.Append(map.TryGetValue(c, out r) ? r : c);
        }
        return sb.ToString();
    }
}
