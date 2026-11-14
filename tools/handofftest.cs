// 换台交接（handoff）一次性验证工具，不是产品代码。
// 用法：handofftest.exe <视频A> <视频B>
// 期望：第二次 Start 返回时新窗口已上屏，进程数始终为 1，旧进程被收掉。
using System;
using System.Diagnostics;
using System.Threading;

namespace WallpaperManager
{
    static class HandoffTest
    {
        static int Count()
        {
            try { return Process.GetProcessesByName("WallpaperPlayer").Length; }
            catch { return -1; }
        }

        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("用法：handofftest.exe <视频A> <视频B>");
                return;
            }

            Dynamic.MountMode = 8;
            Console.WriteLine("开始前播放器进程数 = " + Count() + "，窗口存在 = " + Dynamic.WindowAlive);

            string err;
            Stopwatch sw = Stopwatch.StartNew();
            bool a = Dynamic.Start(args[0], 30, out err);
            sw.Stop();
            Console.WriteLine("A 启动=" + a + " 耗时=" + sw.ElapsedMilliseconds + "ms " + err
                              + " 进程数=" + Count() + " 窗口=" + Dynamic.WindowAlive);

            Thread.Sleep(3500);
            Console.WriteLine("A 稳定后：进程数=" + Count() + " 窗口=" + Dynamic.WindowAlive);
            Thread.Sleep(500);

            sw.Restart();
            bool b = Dynamic.Start(args[1], 30, out err);
            sw.Stop();
            Console.WriteLine("B 启动=" + b + " 交接耗时=" + sw.ElapsedMilliseconds + "ms " + err
                              + " 进程数=" + Count() + " 窗口=" + Dynamic.WindowAlive);

            Thread.Sleep(2500);
            Console.WriteLine("B 稳定后：进程数=" + Count() + " 窗口=" + Dynamic.WindowAlive);

            Dynamic.Stop();
            Thread.Sleep(600);
            Console.WriteLine("停止后：进程数=" + Count() + " 窗口=" + Dynamic.WindowAlive);
        }
    }
}
