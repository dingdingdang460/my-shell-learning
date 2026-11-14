using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WallpaperManager
{
    /// <summary>系统壁纸相关：设置壁纸、DPI 感知、读取当前壁纸</summary>
    public static class Desk
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        const int SPI_SETDESKWALLPAPER = 0x0014;
        const int SPIF_UPDATEINIFILE = 0x01;
        const int SPIF_SENDCHANGE = 0x02;

        public static void EnableDpiAwareness()
        {
            try { SetProcessDPIAware(); }
            catch { }
        }

        /// <summary>
        /// 设置桌面壁纸。fitMode: 10=填充, 6=适应, 2=拉伸, 0=居中(平铺取 0 并置 TileWallpaper=1)
        /// </summary>
        public static bool SetWallpaper(string imagePath, int fitMode)
        {
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return false;

            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                {
                    if (k != null)
                    {
                        k.SetValue("WallpaperStyle", fitMode.ToString(), RegistryValueKind.String);
                        k.SetValue("TileWallpaper", "0", RegistryValueKind.String);
                    }
                }
            }
            catch { }

            int ok = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, imagePath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            return ok != 0;
        }

        /// <summary>读注册表里当前壁纸路径（用于界面显示）</summary>
        public static string GetCurrentWallpaper()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                {
                    if (k == null) return "";
                    object v = k.GetValue("Wallpaper");
                    return v == null ? "" : Convert.ToString(v);
                }
            }
            catch { return ""; }
        }
    }
}
