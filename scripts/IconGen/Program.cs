using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace IconGen;

internal static class Program
{
    private static int Main(string[] args)
    {
        var outPath = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "app.ico");

        int[] sizes = { 16, 24, 32, 48, 64, 128 };
        var frames = new List<byte[]>(sizes.Length);

        foreach (int s in sizes)
        {
            using var bmp = RenderIcon(s);
            // BMP-in-ICO：32bpp BGRA + AND mask（32bpp 时 mask 通常全 0）。
            // 不用 PNG 嵌入，因为 PNG frame 在 WPF 转 HICON 时部分尺寸会抛 E_HANDLE。
            frames.Add(BuildBmpInIcoFrame(bmp));
        }

        using var ico = new MemoryStream();
        using (var bw = new BinaryWriter(ico))
        {
            bw.Write((ushort)0);                // reserved
            bw.Write((ushort)1);                // type = ICO
            bw.Write((ushort)sizes.Length);

            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                byte[] data = frames[i];
                bw.Write((byte)s);              // width
                bw.Write((byte)s);              // height
                bw.Write((byte)0);              // color count
                bw.Write((byte)0);              // reserved
                bw.Write((ushort)1);            // planes
                bw.Write((ushort)32);           // bit count
                bw.Write((uint)data.Length);    // size
                bw.Write((uint)offset);         // offset
                offset += data.Length;
            }
            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write(frames[i]);
            }
            bw.Flush();
        }

        var icoBytes = ico.ToArray();
        File.WriteAllBytes(outPath, icoBytes);
        Console.WriteLine($"Wrote {outPath} ({icoBytes.Length} bytes, {sizes.Length} sizes)");
        return 0;
    }

    /// <summary>蓝色扁平信封：圆角蓝色背景 + 白色信封 + V 形封口（透出蓝色）。
    /// 颜色与 Themes/AppTheme.xaml 的 Brush_Accent_Default (#2563EB) 一致。</summary>
    private static Bitmap RenderIcon(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var accent = Color.FromArgb(255, 37, 99, 235); // #2563EB (Accent.Default)

        // 蓝色圆角矩形背景
        int bgRadius = (int)Math.Round(s * 0.22);
        using (var bgPath = RoundedRectPath(0, 0, s, s, bgRadius))
        {
            g.FillPath(new SolidBrush(accent), bgPath);
        }

        // 信封主体：白色圆角矩形
        int envMargin = (int)Math.Round(s * 0.18);
        int envTop = (int)Math.Round(s * 0.30);
        int envHeight = (int)Math.Round(s * 0.44);
        int envWidth = s - 2 * envMargin;
        int envRadius = (int)Math.Round(s * 0.06);
        using (var envPath = RoundedRectPath(envMargin, envTop, envWidth, envHeight, envRadius))
        {
            g.FillPath(new SolidBrush(Color.White), envPath);
        }

        // V 形封口：蓝色三角形覆盖信封顶部（视觉上像打开的信封盖）
        int foldDepth = (int)Math.Round(s * 0.20);
        float midX = envMargin + envWidth / 2f;
        float midY = envTop + foldDepth;
        var foldPoints = new[]
        {
            new PointF(envMargin, envTop),
            new PointF(envMargin + envWidth, envTop),
            new PointF(midX, midY),
        };
        g.FillPolygon(new SolidBrush(accent), foldPoints);

        g.Dispose();
        return bmp;
    }

    private static GraphicsPath RoundedRectPath(int x, int y, int w, int h, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// 把 32bpp ARGB bitmap 编码成 ICO 帧：BITMAPINFOHEADER（height=2*实际）+ BGRA 像素 + AND mask。
    /// AND mask 对 32bpp 图标一般全 0（alpha 已表达透明），但 ICO 格式要求至少 1 行对齐到 32-bit。
    /// </summary>
    private static byte[] BuildBmpInIcoFrame(Bitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;

        // 像素行 stride 必须 4 字节对齐
        int pixelRowBytes = w * 4;
        int andMaskRowBytes = (w + 31) / 32 * 4;
        int pixelBytes = pixelRowBytes * h;
        int andMaskBytes = andMaskRowBytes * h;
        int total = 40 + pixelBytes + andMaskBytes;

        var ms = new MemoryStream(total);
        var bw = new BinaryWriter(ms);

        // BITMAPINFOHEADER
        bw.Write((uint)40);                 // biSize
        bw.Write(w);                        // biWidth
        bw.Write(h * 2);                    // biHeight（ICO 约定：含 AND mask 高度）
        bw.Write((ushort)1);                // biPlanes
        bw.Write((ushort)32);               // biBitCount
        bw.Write((uint)0);                  // biCompression = BI_RGB
        bw.Write((uint)0);                  // biSizeImage（0 也可）
        bw.Write(0);                        // biXPelsPerMeter
        bw.Write(0);                        // biYPelsPerMeter
        bw.Write((uint)0);                  // biClrUsed
        bw.Write((uint)0);                  // biClrImportant

        // 提取像素为 BGRA bottom-up
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            // 从底向上复制（BITMAP 是 bottom-up）
            int srcStride = data.Stride;
            var rowBuf = new byte[pixelRowBytes];
            for (int y = h - 1; y >= 0; y--)
            {
                IntPtr srcRow = IntPtr.Add(data.Scan0, y * srcStride);
                System.Runtime.InteropServices.Marshal.Copy(srcRow, rowBuf, 0, pixelRowBytes);
                // GDI+ 给的是 BGRA（Format32bppArgb 实际内存是 BGRA），无需通道交换
                bw.Write(rowBuf, 0, pixelRowBytes);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        // AND mask：全 0（透明位留 0 = 完全可见；alpha=0 处会显示透明）
        var maskBuf = new byte[andMaskBytes];
        bw.Write(maskBuf, 0, andMaskBytes);

        bw.Flush();
        var bytes = ms.ToArray();
        bw.Dispose();
        ms.Dispose();
        return bytes;
    }
}