using System.Drawing;
using System.Drawing.Drawing2D;

// แมวส้มพิกเซลอาร์ต 16x16 (วาดสด ไม่ใช้ไฟล์รูป)
static class Mascot
{
    public const int Size = 16;

    static readonly string[] Pixels =
    {
        "..oo........oo..",
        ".obbo......obbo.",
        ".obpbo....obpbo.",
        ".obbbboooobbbbo.",
        "obbbbbbbbbbbbbbo",
        "obbbbbbbbbbbbbbo",
        "obbwebbbbbbwebbo",
        "obbeebbbbbbeebbo",
        "obppbbbnnbbbppbo",
        "obbbbbnbbnbbbbbo",
        ".obbbbbnnbbbbbo.",
        ".oggggggggggggo.",
        ".obbblllllbbbbo.",
        ".obbblllllbbbbo.",
        ".obbobbbbbbobbo.",
        "..oo.oooooo.oo..",
    };

    static Color Col(char c, bool gray)
    {
        switch (c)
        {
            case 'o': return Color.FromArgb(59, 36, 23);
            case 'b': return gray ? Color.FromArgb(156, 163, 175) : Color.FromArgb(244, 162, 89);
            case 'l': return gray ? Color.FromArgb(209, 213, 219) : Color.FromArgb(255, 226, 189);
            case 'p': return gray ? Color.FromArgb(196, 181, 189) : Color.FromArgb(255, 158, 177);
            case 'n': return Color.FromArgb(224, 96, 126);
            case 'e': return Color.FromArgb(43, 26, 16);
            case 'w': return Color.White;
            case 'g': return gray ? Color.FromArgb(107, 114, 128) : Color.FromArgb(63, 143, 90);
        }
        return Color.Transparent;
    }

    // eyesClosed = กระพริบตา / หลับ
    public static void Draw(Graphics g, float x, float y, float px, bool eyesClosed, bool gray)
    {
        var oldSmooth = g.SmoothingMode;
        var oldOffset = g.PixelOffsetMode;
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        for (int r = 0; r < Size; r++)
        {
            string row = Pixels[r];
            for (int c = 0; c < Size; c++)
            {
                char ch = row[c];
                if (ch == '.') continue;
                if (eyesClosed && (ch == 'e' || ch == 'w')) ch = r == 7 ? 'o' : 'b';
                using (var b = new SolidBrush(Col(ch, gray)))
                    g.FillRectangle(b, x + c * px, y + r * px, px, px);
            }
        }
        g.SmoothingMode = oldSmooth;
        g.PixelOffsetMode = oldOffset;
    }

    // วาดตามขนาดไอคอนถาดจริงของจอ ไม่ให้ Windows ย่อจนเบลอ
    public static Icon MakeIcon(bool gray, int size)
    {
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
            Draw(g, 0, 0, size / (float)Size, gray, gray);
        return Icon.FromHandle(bmp.GetHicon());
    }
}
