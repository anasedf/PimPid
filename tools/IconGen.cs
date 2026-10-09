using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

// สร้าง PimPid.ico (หลายขนาด) จากแมวพิกเซล ใช้ตอน build เท่านั้น
static class IconGen
{
    static void Main(string[] args)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var pngs = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        {
            using (var bmp = new Bitmap(sizes[i], sizes[i], PixelFormat.Format32bppArgb))
            using (var ms = new MemoryStream())
            {
                using (var g = Graphics.FromImage(bmp))
                    Mascot.Draw(g, 0, 0, sizes[i] / (float)Mascot.Size, false, false);
                bmp.Save(ms, ImageFormat.Png);
                pngs[i] = ms.ToArray();
            }
        }
        using (var w = new BinaryWriter(File.Create(args[0])))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }
    }
}
