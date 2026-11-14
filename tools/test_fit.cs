// 竖屏壁纸自适应的离线验证：造一张「上红下蓝」的竖图，
// 走一遍 WallpaperFit.Prepare，再从输出图里找红/蓝 —— 两种颜色都在，
// 说明整张图（头到尾）都进了画面，而不是被填充裁掉中间一条。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using WallpaperManager;

static class FitTest
{
    static int fails;

    static void Check(bool cond, string what)
    {
        Console.WriteLine((cond ? "  [PASS] " : "  [FAIL] ") + what);
        if (!cond) fails++;
    }

    static int Main()
    {
        int sw = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
        int sh = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;
        Console.WriteLine("屏幕 " + sw + "x" + sh);

        string dir = Path.Combine(Path.GetTempPath(), "fit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            // ---------- 1. 竖图（9:24） ----------
            string portrait = Path.Combine(dir, "portrait.jpg");
            MakeStriped(portrait, 900, 2400);

            int fit;
            string note;
            string outA = WallpaperFit.Prepare(portrait, out fit, out note);
            Console.WriteLine("竖图 → " + outA);
            Console.WriteLine("       fit=" + fit + "  note=" + note);

            Check(!Same(outA, portrait), "竖图应被换成适配图，而不是原图直接填充");
            Check(fit == 10, "适配图配套的 fitMode 应为 10（填充），实际 " + fit);
            Check(File.Exists(outA), "适配图已落盘");

            if (File.Exists(outA))
            {
                using (Image o = Image.FromFile(outA))
                {
                    Check(o.Width == sw && o.Height == sh,
                          "适配图尺寸应等于屏幕：" + o.Width + "x" + o.Height);

                    bool red, blue, dark;
                    Scan(o, out red, out blue, out dark);
                    Check(red, "画面里能看到原图顶部（红）");
                    Check(blue, "画面里能看到原图底部（蓝）");
                    Check(!dark, "原图两侧应由模糊底衬铺满，而不是死黑边");
                }
            }

            // ---------- 2. 缓存命中 ----------
            string again = WallpaperFit.Prepare(portrait, out fit, out note);
            Check(Same(again, outA), "同一张图第二次应命中缓存、复用同一路径");

            // ---------- 3. 和屏幕同比例的图 ----------
            string landscape = Path.Combine(dir, "landscape.jpg");
            MakeStriped(landscape, sw, sh);
            string outB = WallpaperFit.Prepare(landscape, out fit, out note);
            Check(Same(outB, landscape), "屏幕同比例的图应直接用原图（不做无谓重编码）");
            Check(fit == 10, "同比例图 fitMode 应为 10，实际 " + fit);

            // ---------- 4. 完全合不上的比例（超宽图） ----------
            string wide = Path.Combine(dir, "wide.jpg");
            MakeStriped(wide, 3200, 600);
            string outC = WallpaperFit.Prepare(wide, out fit, out note);
            Check(!Same(outC, wide) && File.Exists(outC), "超宽图也应走自适应");
            if (File.Exists(outC))
            {
                using (Image o = Image.FromFile(outC))
                {
                    Check(o.Width == sw && o.Height == sh, "超宽图输出尺寸应等于屏幕");
                }
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine(fails == 0 ? "全部通过" : (fails + " 项失败"));
        return fails == 0 ? 0 : 1;
    }

    static bool Same(string a, string b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>造一张顶部红、底部蓝、中间灰的图（上下各 12% 高度）</summary>
    static void MakeStriped(string path, int w, int h)
    {
        using (Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb))
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(Color.FromArgb(120, 120, 120));
            int band = Math.Max(8, h / 8);
            using (SolidBrush red = new SolidBrush(Color.FromArgb(255, 0, 0)))
                g.FillRectangle(red, 0, 0, w, band);
            using (SolidBrush blue = new SolidBrush(Color.FromArgb(0, 0, 255)))
                g.FillRectangle(blue, 0, h - band, w, band);

            ImageCodecInfo jpg = null;
            ImageCodecInfo[] all = ImageCodecInfo.GetImageEncoders();
            for (int i = 0; i < all.Length; i++)
                if (all[i].FormatID == ImageFormat.Jpeg.Guid) { jpg = all[i]; break; }

            if (jpg == null) b.Save(path, ImageFormat.Jpeg);
            else
            {
                using (EncoderParameters p = new EncoderParameters(1))
                {
                    p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
                    b.Save(path, jpg, p);
                }
            }
        }
    }

    /// <summary>粗扫一遍：有没有明显的红/蓝；左边缘是不是接近纯黑</summary>
    static void Scan(Image img, out bool red, out bool blue, out bool darkEdge)
    {
        using (Bitmap b = new Bitmap(img))
        {
            int w = b.Width, h = b.Height;
            red = false;
            blue = false;

            for (int y = 0; y < h; y += 3)
            {
                for (int x = 0; x < w; x += 3)
                {
                    Color c = b.GetPixel(x, y);
                    if (!red && c.R > 140 && c.G < 90 && c.B < 90) red = true;
                    if (!blue && c.B > 140 && c.R < 90 && c.G < 90) blue = true;
                    if (red && blue) break;
                }
                if (red && blue) break;
            }

            long sum = 0;
            int n = 0;
            int xEnd = Math.Max(6, w / 40);
            for (int y = h / 3; y < h * 2 / 3; y += 3)
            {
                for (int x = 1; x < xEnd; x += 2)
                {
                    Color c = b.GetPixel(x, y);
                    sum += (c.R + c.G + c.B) / 3;
                    n++;
                }
            }
            darkEdge = n > 0 && (sum / n) < 10;
        }
    }
}
