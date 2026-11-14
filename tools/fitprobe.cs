// 端到端：在 DPI 感知的进程里造一张竖图 → 自适应 → 真的设成系统壁纸 → 抓屏看结果。
// 只看两件事：
//   1. 合成图的尺寸是不是屏幕的「物理像素」（不是 125% 缩放后的逻辑尺寸）
//   2. 抓回来的屏幕上，原图的头（红）和尾（蓝）是不是都在，两侧是不是底衬而不是黑
// 跑完会把原壁纸恢复回去。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using WallpaperManager;

static class FitProbe
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);

    const uint SRCCOPY = 0x00CC0020;
    const uint CAPTUREBLT = 0x40000000;

    static int fails;

    static void Check(bool cond, string what)
    {
        Console.WriteLine((cond ? "  [PASS] " : "  [FAIL] ") + what);
        if (!cond) fails++;
    }

    static int Main(string[] args)
    {
        Desk.EnableDpiAwareness();   // 和 WallpaperManager.exe 完全一样的启动条件

        int sw = GetSystemMetrics(0);
        int sh = GetSystemMetrics(1);
        Console.WriteLine("DPI 感知后屏幕 = " + sw + "x" + sh);

        // 用法：fitprobe.exe [图片路径] [apply]
        //   给图片路径就用它（不再造测试图）；再加 apply 才真的设成系统壁纸。
        string given = "";
        bool apply = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "apply", StringComparison.OrdinalIgnoreCase)) apply = true;
            else if (!string.IsNullOrEmpty(args[i])) given = args[i];
        }

        string dir = Path.Combine(Path.GetTempPath(), "fit-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string portrait = string.IsNullOrEmpty(given)
            ? Path.Combine(dir, "portrait.jpg")
            : given;
        string shot = Path.Combine(Environment.CurrentDirectory, "fit-screen.png");

        string old = Desk.GetCurrentWallpaper();
        Console.WriteLine("原壁纸 = " + (string.IsNullOrEmpty(old) ? "(空)" : old));

        try
        {
            if (string.IsNullOrEmpty(given)) MakeStriped(portrait, 1080, 2400);   // 标准手机竖屏 9:20

            int fit;
            string note;
            string outp = WallpaperFit.Prepare(portrait, out fit, out note);
            Console.WriteLine("适配 → " + outp + "  fit=" + fit + "  note=" + note);

            string preview = Path.Combine(Environment.CurrentDirectory, "fit-preview.jpg");
            try { if (!string.Equals(outp, portrait, StringComparison.OrdinalIgnoreCase)) File.Copy(outp, preview, true); else File.Copy(portrait, preview, true); }
            catch { }
            Console.WriteLine("合成图已复制到 " + preview);

            using (Image o = Image.FromFile(outp))
            {
                Check(o.Width == sw && o.Height == sh,
                      "合成图尺寸应等于屏幕物理像素：" + o.Width + "x" + o.Height + " vs " + sw + "x" + sh);
            }

            if (!apply)
            {
                Console.WriteLine("（没加 apply，不改系统壁纸）");
                Console.WriteLine(fails == 0 ? "PROBE OK" : (fails + " 项失败"));
                return fails == 0 ? 0 : 1;
            }

            Console.WriteLine("设为壁纸…");
            bool ok = Desk.SetWallpaper(outp, fit);
            Check(ok, "SPI_SETDESKWALLPAPER 返回成功");
            Thread.Sleep(2600);

            using (Bitmap b = Grab(sw, sh))
            {
                b.Save(shot, ImageFormat.Png);

                // 红/蓝断言只对程序自己造的测试图有意义；真实照片只看抓屏
                if (string.IsNullOrEmpty(given))
                {
                    bool red, blue, dark;
                    Scan(b, out red, out blue, out dark);
                    Check(red, "屏幕上能看到原图顶部（红）");
                    Check(blue, "屏幕上能看到原图底部（蓝）");
                    Check(!dark, "两侧是底衬，不是黑边");
                }
            }
            Console.WriteLine("抓屏：" + shot);
        }
        finally
        {
            if (apply && !string.IsNullOrEmpty(old) && File.Exists(old))
            {
                Desk.SetWallpaper(old, 10);
                Console.WriteLine("已恢复原壁纸");
            }
            else
            {
                Console.WriteLine("没有可恢复的原壁纸（原先就是纯色/空）");
            }
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine(fails == 0 ? "PROBE OK" : (fails + " 项失败"));
        return fails == 0 ? 0 : 1;
    }

    static Bitmap Grab(int w, int h)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            bool ok = BitBlt(mem, 0, 0, w, h, screen, 0, 0, SRCCOPY | CAPTUREBLT);
            if (!ok) throw new Exception("BitBlt 失败");
            return Image.FromHbitmap(bmp);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    static void MakeStriped(string path, int w, int h)
    {
        using (Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb))
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(Color.FromArgb(128, 128, 128));
            int band = h / 8;
            using (SolidBrush red = new SolidBrush(Color.FromArgb(255, 0, 0)))
                g.FillRectangle(red, 0, 0, w, band);
            using (SolidBrush blue = new SolidBrush(Color.FromArgb(0, 0, 255)))
                g.FillRectangle(blue, 0, h - band, w, band);

            ImageCodecInfo jpg = null;
            ImageCodecInfo[] all = ImageCodecInfo.GetImageEncoders();
            for (int i = 0; i < all.Length; i++)
                if (all[i].FormatID == ImageFormat.Jpeg.Guid) { jpg = all[i]; break; }

            using (EncoderParameters p = new EncoderParameters(1))
            {
                p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
                if (jpg != null) b.Save(path, jpg, p);
                else b.Save(path, ImageFormat.Jpeg);
            }
        }
    }

    static void Scan(Bitmap b, out bool red, out bool blue, out bool darkEdge)
    {
        int w = b.Width, h = b.Height;
        red = false;
        blue = false;

        int rowRed = -1, rowBlue = -1;
        for (int y = 0; y < h; y += 4)
        {
            int rc = 0, bc = 0;
            for (int x = 0; x < w; x += 4)
            {
                Color c = b.GetPixel(x, y);
                if (c.R > 130 && c.G < 90 && c.B < 90) rc++;
                if (c.B > 130 && c.R < 90 && c.G < 90) bc++;
            }
            if (rowRed < 0 && rc > 8) rowRed = y;
            if (bc > 8) rowBlue = y;
        }
        red = rowRed >= 0;
        blue = rowBlue >= 0;
        if (red && blue)
        {
            Console.WriteLine("      红带 y=" + rowRed + "，蓝带 y=" + rowBlue
                              + "（画面高 " + h + "）");
        }

        long sum = 0;
        int n = 0;
        int xEnd = Math.Max(6, w / 40);
        for (int y = h / 3; y < h * 2 / 3; y += 4)
        {
            for (int x = 1; x < xEnd; x += 2)
            {
                Color c = b.GetPixel(x, y);
                sum += (c.R + c.G + c.B) / 3;
                n++;
            }
        }
        darkEdge = n > 0 && (sum / n) < 8;
        if (n > 0) Console.WriteLine("      左边缘平均亮度 " + (sum / n) + "/255");
    }
}
