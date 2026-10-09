using System;
using System.Drawing;
using System.Runtime.InteropServices;

// ถือภาพของ layered window ไว้ จะได้เปลี่ยนแค่ความโปร่งใส/ตำแหน่งได้โดยไม่ต้องส่งภาพใหม่ทุกเฟรม
class LayeredSurface : IDisposable
{
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int flags);
    [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow")] static extern bool UpdateLayeredPos(IntPtr hwnd,
        IntPtr hdcDst, ref POINT pptDst, IntPtr psize, IntPtr hdcSrc, IntPtr pptSrc, int crKey, ref BLENDFUNCTION pblend, int flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    IntPtr dc, bmp, original;
    int width, height;
    bool fresh;

    public void SetImage(Bitmap image)
    {
        if (dc == IntPtr.Zero) dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr h = image.GetHbitmap(Color.FromArgb(0));
        IntPtr prev = SelectObject(dc, h);
        if (bmp == IntPtr.Zero) original = prev; else DeleteObject(bmp);
        bmp = h;
        width = image.Width;
        height = image.Height;
        fresh = true;
    }

    // ส่งภาพใหม่ก็ต่อเมื่อมีการ SetImage ตั้งแต่ครั้งก่อน ไม่งั้นขยับ/เปลี่ยน alpha อย่างเดียว (ถูกมาก)
    public void Push(IntPtr hwnd, int x, int y, byte alpha)
    {
        if (dc == IntPtr.Zero) return;
        var dst = new POINT { x = x, y = y };
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
        if (fresh)
        {
            var size = new SIZE { cx = width, cy = height };
            var src = new POINT();
            UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref size, dc, ref src, 0, ref blend, 2);
            fresh = false;
        }
        else UpdateLayeredPos(hwnd, IntPtr.Zero, ref dst, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, 2);
    }

    public void Dispose()
    {
        if (dc == IntPtr.Zero) return;
        SelectObject(dc, original);
        DeleteObject(bmp);
        DeleteDC(dc);
        dc = bmp = IntPtr.Zero;
    }
}
