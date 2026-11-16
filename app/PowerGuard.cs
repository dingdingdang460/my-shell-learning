using System;
using System.Runtime.InteropServices;

namespace WallpaperManager
{
    /// <summary>
    /// 功耗守卫（11-16 新增）。
    ///
    /// 笔记本拔电后，30-60fps 的视频壁纸会持续吃电；以前程序里只有一行写着
    /// 「功耗守卫」的注释，实际一个字节的检测代码都没有。这里补上真正的判定：
    /// 电池供电且电量低于阈值时暂停动态壁纸解码，插电或电量回升再恢复。
    ///
    /// 判定逻辑本身是纯函数 Decide(...)，自检 --guardtest 能覆盖每种组合；
    /// 真实读取只在定时器里走 GetSystemPowerStatus。
    /// </summary>
    public static class PowerGuard
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SystemPowerStatus
        {
            public byte ACLineStatus;        // 0=电池 1=交流 255=未知
            public byte BatteryFlag;         // 1=高 2=低 4=严重 8=充电中 128=无电池 255=未知
            public byte BatteryLifePercent;  // 0-100，255=未知
            public byte SystemStatusFlag;
            public int BatteryLifeTime;      // 秒，-1=未知
            public int BatteryFullLifeTime;  // 秒，-1=未知
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        /// <summary>
        /// 读一次当前电源状态。读不到返回 false，调用方按「不暂停」处理 ——
        /// 宁可多耗一点电，也不要因为读不到状态就把壁纸突然停掉。
        /// </summary>
        public static bool TryRead(out bool onBattery, out int percent, out string why)
        {
            onBattery = false;
            percent = 255;
            why = "";
            try
            {
                SystemPowerStatus s;
                if (!GetSystemPowerStatus(out s))
                {
                    why = "GetSystemPowerStatus 调用失败";
                    return false;
                }
                onBattery = (s.ACLineStatus == 0);
                percent = s.BatteryLifePercent;
                return true;
            }
            catch (Exception ex)
            {
                why = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 纯判定，不碰系统状态：
        ///   acLine    0=电池 1=交流 其它=未知
        ///   percent   0-100，255=未知
        ///   threshold 低于等于这个百分比就暂停（0-100，越界会夹到区间内）
        ///
        /// 只有「开着守卫 + 确实在用电池 + 电量已知且不高于阈值」才暂停。
        /// 电量未知（255）不暂停；插着电不暂停。
        /// </summary>
        public static bool Decide(int acLine, int percent, int threshold, bool pauseOnBattery, out string why)
        {
            why = "";
            if (!pauseOnBattery) return false;
            if (acLine != 0) return false;
            if (percent < 0 || percent > 100) return false;

            if (threshold < 0) threshold = 0;
            if (threshold > 100) threshold = 100;
            if (percent > threshold) return false;

            why = "电池供电 " + percent + "%（阈值 " + threshold + "%）";
            return true;
        }

        /// <summary>真实环境便捷封装：读状态 + 判定。读不到就返回 false。</summary>
        public static bool ShouldPause(bool pauseOnBattery, int threshold, out string why)
        {
            bool onBattery;
            int percent;
            string readWhy;
            if (!TryRead(out onBattery, out percent, out readWhy))
            {
                why = readWhy;
                return false;
            }
            return Decide(onBattery ? 0 : 1, percent, threshold, pauseOnBattery, out why);
        }
    }
}
