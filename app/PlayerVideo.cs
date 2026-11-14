using System;
using System.Threading;

namespace WallpaperManager
{
    /// <summary>
    /// 视频模式：把 mp4 真正播进桌面壁纸层。
    ///
    /// 链路：MF SourceReader 解出 RGB32 → GDI 缩放拷进屏幕大小的 32bpp 呈现缓冲
    /// → **UpdateLayeredWindow** 提交给 DWM。
    ///
    /// 为什么最后一步是 ULW 而不是普通 GDI 绘制：11-11 11:21 的实机探测（Probe2 v4）
    /// 逐张截图对比过 —— 普通 GDI 子窗口挂 Progman / WorkerW / DefView、无论 Z 序插在哪，
    /// 屏幕上都是 0 像素；只有 UpdateLayeredWindow 提交的位图会真的被合成。
    /// 而 Progman 在 24H2 上是 WS_EX_NOREDIRECTIONBITMAP（没有 GDI 表面），
    /// 所以桌面层能用的恰好只有这条“把整张位图交给 DWM”的通道。
    ///
    /// 挂载层级由 WallpaperPlayer.mountMode 决定，默认挂在 SHELLDLL_DefView 最底层：
    /// 视频占壁纸位置，桌面图标仍然画在它上面。
    /// </summary>
    static class PlayerVideo
    {
        // 把系统定时器精度提到 1ms：单线程播放下靠 Thread.Sleep 控制帧间隔，
        // 默认 15.6ms 的粒度会把 33ms 的帧拖成 45ms（掉到 22fps 左右）。
        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        static extern uint TimeBeginPeriod(uint ms);

        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        static extern uint TimeEndPeriod(uint ms);

        public static void Play(string path, int fpsHint)
        {
            TimeBeginPeriod(1);
            try { PlayCore(path, fpsHint); }
            finally { TimeEndPeriod(1); }
        }

        static void PlayCore(string path, int fpsHint)
        {
            Log.Note("[V] 进入视频模式：" + path + (fpsHint > 0 ? "（期望 " + fpsHint + " fps）" : ""));

            if (!PlayerProgram.InitDesktop())
            {
                PlayerProgram.Fatal("桌面环境不可用（Progman/DefView 都没找到），无法把视频画到桌面上。");
                return;
            }
            PlayerProgram.CloseLeftovers();

            MfVideo.Reader reader = null;
            try
            {
                reader = MfVideo.Reader.Open(path);
                Log.Note("[V] 解码器就绪 " + reader.Width + "x" + reader.Height + " stride=" + reader.Stride);
            }
            catch (Exception ex)
            {
                PlayerProgram.Fatal("这个视频解不开：\r\n" + ex.Message + "\r\n\r\n换个 mp4 试试，或者告诉我它的编码格式。");
                return;
            }

            // 洋红自检只对 UpdateLayeredWindow 这条路有意义（它靠一次顶层提交判断混合参数对不对）。
            // mountMode 8 走的是「分层子窗口 + GDI 直画」，自检窗口反而会闪一下红屏干扰判断，跳过。
            if (PlayerProgram.MountMode == 8)
                Log.Note("[V] 挂载模式 8：跳过 ULW 自检（改为分层子窗口 + GDI 直画）");
            else
            {
                bool channelOk = PlayerProgram.UlwSelfCheck();
                if (!channelOk)
                    Log.Note("[V] !! 上屏通道自检未通过，仍然继续（下面窗口一旦被合成，画面就是黑的）");
            }

            if (!PlayerProgram.BufferInit(reader.Width, reader.Height))
            {
                PlayerProgram.Fatal("帧缓冲创建失败（内存不足？）。");
                reader.Dispose();
                return;
            }
            // 呈现缓冲是屏幕大小：帧在它里面按「填充裁切」缩放，再由 ULW 整块交给 DWM。
            // mountMode 8 用 GDI 直画，不需要这块缓冲。
            if (PlayerProgram.MountMode != 8 && !PlayerProgram.UlwPrepare(PlayerProgram.ScreenW, PlayerProgram.ScreenH))
            {
                PlayerProgram.Fatal("呈现缓冲创建失败，无法提交画面。");
                reader.Dispose();
                return;
            }

            // 先铺一帧纯黑并提交：分层窗口必须先有一份内容，才能翻显示位；
            // 顺序反了 DWM 会记住空表面，后面再怎么提交都不显示。
            PlayerProgram.FillBuffer(0, 0, 0);

            string err;
            IntPtr hwnd = PlayerProgram.CreateLayerChild(PlayerProgram.ClsVideo, "WallpaperPlayerVideo",
                                                         PlayerProgram.ScreenW, PlayerProgram.ScreenH, out err);
            if (hwnd == IntPtr.Zero)
            {
                PlayerProgram.Fatal("挂到桌面层失败：" + err);
                reader.Dispose();
                return;
            }

            bool ok = PlayerProgram.PresentFrame(hwnd);
            Log.Note("[V] 首帧提交 " + (ok ? "成功" : "失败 err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
            PlayerProgram.ShowLayerWindow(hwnd);
            PlayerProgram.DumpProgmanOrder("[V] 显示后");

            // 注意：从本进程抓屏抓不到桌面层的合成结果（11:21 实测：屏幕上明明有画面，
            // StretchBlt 抓回来的还是原壁纸），所以这里不再用命中率判断成败，直接开播。
            // 关键：解码**就在本线程**跑，不再开子线程。
            // 上一版把 reader 建在主线程、帧循环放子线程，跨 COM 套间调用直接把 reader
            // 判成 E_NOINTERFACE（日志：「无法将 System.__ComObject 强制转换为 IMFSourceReader」）。
            // 现在整条链路（Media Foundation / DIB / UpdateLayeredWindow / 窗口）都在同一个线程上。
            string loopPath = path;
            DecodeLoop(ref reader, loopPath, hwnd);

            PlayerProgram.StopRequested = true;
            try { if (reader != null) reader.Dispose(); } catch { }
            PlayerProgram.UlwRelease();
            PlayerProgram.RefreshDesktop();
            Log.Note("[V] 播放结束");
        }

        static void DecodeLoop(ref MfVideo.Reader reader, string path, IntPtr hwnd)
        {
            long baseMs = -1;           // 首帧的媒体时间
            long startTick = 0;         // 首帧的墙上时间
            long frames = 0;
            long ulwFail = 0;
            bool firstFrameChecked = false;
            bool firstPresentChecked = false;
            long statStart = Environment.TickCount;
            long lastErrTick = 0;

            while (!PlayerProgram.StopRequested)
            {
                // 暂停/继续/停止指令就在本线程的消息队列里，帧循环里要主动分发，
                // 否则托盘点了没反应（以前是靠独立的消息循环线程）
                PlayerProgram.PumpOnce();

                if (PlayerProgram.Paused)
                {
                    int t0 = Environment.TickCount;
                    int lastPaint = 0;
                    while (PlayerProgram.Paused && !PlayerProgram.StopRequested)
                    {
                        PlayerProgram.PumpOnce();
                        // 暂停时每 500ms 把最后一帧重画一次：
                        // 桌面被别的程序触发重绘时，窗口表面可能被擦掉，
                        // 不补画就会露出系统壁纸 —— 看起来像"闪一下又回来"。
                        if (Environment.TickCount - lastPaint > 500)
                        {
                            lastPaint = Environment.TickCount;
                            PlayerProgram.PresentFrame(hwnd);
                        }
                        Thread.Sleep(30);
                    }
                    startTick += Environment.TickCount - t0;   // 暂停时长不算进时间轴
                    continue;
                }

                long ts;
                bool got;
                try
                {
                    got = reader.NextFrame(PlayerProgram.FrameBits, PlayerProgram.FrameStride, out ts);
                }
                catch (Exception ex)
                {
                    Log.Note("!!! 解码异常：" + ex.Message);
                    break;
                }

                if (!got)
                {
                    // 播完一轮后的循环。
                    // 以前这里调 IMFSourceReader::SetCurrentPosition 回到 0，
                    // 实测**进程会静默消失**：日志停在"播到末尾"，之后连异常都没有，
                    // 桌面层窗口随之销毁 → 露出系统壁纸 → 看门狗再拉回来，
                    // 用户看到的就是"时不时闪一下系统默认壁纸"。
                    // 现在改成重开一个解码器：多花约 0.2 秒，但绝不再崩。
                    // 这段时间窗口保留最后一帧，画面不会空。
                    Log.Note("[V] 播完一轮，重开解码器继续循环");
                    try { reader.Dispose(); } catch { }
                    reader = null;
                    try
                    {
                        reader = MfVideo.Reader.Open(path);
                    }
                    catch (Exception ex)
                    {
                        Log.Note("!!! 重开解码器失败：" + ex.Message);
                        break;
                    }

                    if (reader.Width != PlayerProgram.FrameW || reader.Height != PlayerProgram.FrameH)
                    {
                        Log.Note("[V] 视频尺寸变了 " + reader.Width + "x" + reader.Height + "，重建帧缓冲");
                        if (!PlayerProgram.BufferInit(reader.Width, reader.Height))
                        {
                            Log.Note("!!! 重建帧缓冲失败");
                            break;
                        }
                    }
                    baseMs = -1;
                    continue;
                }

                // 首帧内容自检：直接读帧缓冲中心像素。
                // 这一行能把两类失败彻底分开 ——
                //   全 0/全 16  → 解码器没吐真数据（问题在 MF，不在桌面挂载）
                //   有正常颜色 → 画面是好的，屏幕上看不到就纯是合成/层级问题
                if (!firstFrameChecked)
                {
                    firstFrameChecked = true;
                    int w = reader.Width, h = reader.Height;
                    long off = (long)(h / 2) * PlayerProgram.FrameStride + (long)(w / 2) * 4;
                    int px = System.Runtime.InteropServices.Marshal.ReadInt32(
                        new IntPtr(PlayerProgram.FrameBits.ToInt64() + off));
                    Log.Note("[V] 首帧中心像素 B=" + (px & 0xFF) + " G=" + ((px >> 8) & 0xFF)
                             + " R=" + ((px >> 16) & 0xFF) + " A=" + ((px >> 24) & 0xFF));
                }

                long ms = ts / 10000;      // 100ns → ms
                if (baseMs < 0) { baseMs = ms; startTick = Environment.TickCount; }

                if (PlayerProgram.StopRequested) break;
                if (!PlayerProgram.PresentFrame(hwnd)) ulwFail++;
                frames++;

                // 首帧落地后对比「帧缓冲顶部」与「窗口表面顶部」：
                // 两边都绿 = 画面本来就是绿的（解码/转换问题）；
                // 只有窗口绿 = 绘制没覆盖到顶部（StretchDIBits 目标区不对）。
                if (firstFrameChecked && !firstPresentChecked)
                {
                    firstPresentChecked = true;
                    Log.Note("[V] 顶部像素诊断 帧缓冲: y0=0x" + PlayerProgram.BufferPixel(reader.Width / 2, 0).ToString("x6")
                             + " y5=0x" + PlayerProgram.BufferPixel(reader.Width / 2, 5).ToString("x6")
                             + " y16=0x" + PlayerProgram.BufferPixel(reader.Width / 2, 16).ToString("x6"));
                    Log.Note("[V] 顶部像素诊断 窗口面: y0=0x" + PlayerProgram.WindowPixel(hwnd, PlayerProgram.ScreenW / 2, 0).ToString("x6")
                             + " y5=0x" + PlayerProgram.WindowPixel(hwnd, PlayerProgram.ScreenW / 2, 5).ToString("x6")
                             + " y16=0x" + PlayerProgram.WindowPixel(hwnd, PlayerProgram.ScreenW / 2, 16).ToString("x6")
                             + " 中心=0x" + PlayerProgram.WindowPixel(hwnd, PlayerProgram.ScreenW / 2, PlayerProgram.ScreenH / 2).ToString("x6"));
                }

                // 画完再按这一帧的到期时间补睡：周期 = max(绘制耗时, 帧间隔)，不会越播越慢。
                // 睡着的时候也要继续分发消息，否则暂停/停止要等到下一帧才生效。
                long due = startTick + (ms - baseMs);
                long wait = due - Environment.TickCount;
                if (wait > 0) SleepPumping(Math.Min(wait, 200));
                else if (wait < -400 && Environment.TickCount - lastErrTick > 5000)
                {
                    lastErrTick = Environment.TickCount;
                    Log.Note("[V] 解码跟不上（落后 " + (-wait) + "ms）：这个视频码率偏高或分辨率太大");
                }

                if (Environment.TickCount - statStart >= 5000)
                {
                    int span = Environment.TickCount - (int)statStart;
                    Log.Note("[V] 近 " + (span / 1000) + " 秒画了 " + frames + " 帧（约 "
                             + (frames * 1000 / Math.Max(1, span)) + " fps）"
                             + (ulwFail > 0 ? "，其中 ULW 提交失败 " + ulwFail + " 帧" : ""));
                    frames = 0;
                    ulwFail = 0;
                    statStart = Environment.TickCount;
                }
            }

            Log.Note("[V] 解码循环退出");
        }

        /// <summary>边等边分发消息：单线程播放时不能纯 Thread.Sleep，否则指令要等到下一帧</summary>
        static void SleepPumping(long ms)
        {
            long end = Environment.TickCount + ms;
            while (!PlayerProgram.StopRequested && Environment.TickCount < end)
            {
                PlayerProgram.PumpOnce();
                Thread.Sleep(4);
            }
        }
    }
}
