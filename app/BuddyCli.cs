using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WallpaperManager
{
    /// <summary>
    /// 11-15 深夜：主人要「程序直接请求 WorkBuddy 出片」——
    /// 「我点击生成 → 程序请求 WorkBuddy 生成图生视频」，不要排队等整点领单。
    ///
    /// 查清的事实：
    ///   · 桌面 exe 自己没有模型凭证，不能直接向云端要片；
    ///   · 但客户端自带 @genie/agent-cli（resources/app.asar.unpacked/cli/bin/codebuddy），
    ///     支持 -p 非交互执行、-y 跳过逐次确认，且**内置 VideoGen 工具**
    ///     （bundle 里有 video_gen_tool_result / /model:image-to-video / ProductFeature.VideoGen）；
    ///   · 所以程序可以自己起一个 CLI 会话，让那个会话直接调 VideoGen 出片，
    ///     产物落 videos\&lt;id&gt;.mp4，再走已有的「3 秒扫库 → 自动收单 → 自动播放」。
    ///
    /// 唯一前提：CLI 需要一次授权（浏览器登录），之后一直有效。
    /// 未授权时 CLI 会立刻返回 "Authentication required"，这里如实上报、绝不假装在生成。
    /// </summary>
    static class BuddyCli
    {
        /// <summary>CLI 输出重定向到这个文件，出问题直接看它。</summary>
        public static string LogPath
        {
            get { return Path.Combine(Path.GetTempPath(), "wallpaper-buddicli.log"); }
        }

        public static string LastDetail = "";
        public static int StartCount = 0;

        /// <summary>自检用：真起 node 会消耗积分，自检一律不许真启动。</summary>
        public static bool SelfTestNoStart = false;

        /// <summary>自检用：强制让 TryStart 以这句话失败，用来验「未授权引导」分支。</summary>
        public static string SelfTestForceFail = "";

        /// <summary>自检用：直接指定「是否已授权」，免得自检真去请求模型。</summary>
        public static bool? SelfTestAuthOverride = null;

        // 授权状态缓存：未授权时 CLI 要 ~10 秒才吐 "Authentication required"，
        // 而 TryStart 只盯 6 秒就会误判成「正在出片」（假生成中就是这么来的）。
        // 所以授权与否先由一次探测定下来并缓存，出片入口只认缓存，不去猜。
        static DateTime _probeAt = DateTime.MinValue;
        static bool _probeOk = false;
        static string _probeDetail = "";
        static int _probing = 0;
        const int ProbeFreshMinutes = 10;

        /// <summary>缓存里「已确认授权」就返回 true；没探过或过期都算未确认。</summary>
        public static bool CachedAuthorized(AppConfig cfg, out string detail)
        {
            if (SelfTestAuthOverride.HasValue)
            {
                _probeOk = SelfTestAuthOverride.Value;
                detail = _probeOk ? "自检：已授权" : "自检：未授权";
                return _probeOk;
            }
            if ((DateTime.Now - _probeAt).TotalMinutes <= ProbeFreshMinutes)
            {
                detail = _probeDetail;
                return _probeOk;
            }
            detail = "还没确认过这条通道的授权状态（后台正在探测，稍后重试即可）。";
            return false;
        }

        /// <summary>后台探测一次授权状态，结果进缓存。不占用界面线程。</summary>
        public static void ProbeAsync(AppConfig cfg)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _probing, 1, 0) != 0) return;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string d;
                    bool ok = ProbeAuth(cfg, out d);
                    _probeOk = ok;
                    _probeDetail = d;
                    _probeAt = DateTime.Now;
                }
                catch { }
                finally { System.Threading.Interlocked.Exchange(ref _probing, 0); }
            });
        }

        /// <summary>清掉缓存（授权成功后要立刻重新探测）。</summary>
        public static void InvalidateAuthCache() { _probeAt = DateTime.MinValue; }

        /// <summary>定位客户端安装根目录（由 WorkBuddy.exe 反推）。</summary>
        static string ClientRoot(AppConfig cfg)
        {
            try
            {
                string exe = BuddyBridge.FindClientExe(cfg);
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                    return Path.GetDirectoryName(exe);
            }
            catch { }
            return "";
        }

        /// <summary>CLI 的 JS 入口：&lt;客户端&gt;\resources\app.asar.unpacked\cli\bin\codebuddy</summary>
        public static string ScriptPath(AppConfig cfg)
        {
            string root = ClientRoot(cfg);
            if (root.Length == 0) return "";
            string p = Path.Combine(root, @"resources\app.asar.unpacked\cli\bin\codebuddy");
            return File.Exists(p) ? p : "";
        }

        /// <summary>node.exe：优先客户端自带的便携版，其次 PATH。</summary>
        public static string NodeExe()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    @".workbuddy\binaries\node\versions");
                if (Directory.Exists(dir))
                {
                    string[] subs = Directory.GetDirectories(dir);
                    Array.Sort(subs);
                    for (int i = subs.Length - 1; i >= 0; i--)
                    {
                        string exe = Path.Combine(subs[i], "node.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
            }
            catch { }
            return "node.exe";
        }

        /// <summary>通道是否具备（有 node + 有 CLI 脚本）。登录态只能真跑才知道，不在这里猜。</summary>
        public static bool Available(AppConfig cfg, out string detail)
        {
            string script = ScriptPath(cfg);
            if (script.Length == 0)
            {
                detail = "找不到客户端自带的 codebuddy CLI（WorkBuddy 安装目录里没有 cli 资源）";
                return false;
            }
            string node = NodeExe();
            if (!node.Equals("node.exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(node))
            {
                detail = "找不到 node 运行时";
                return false;
            }
            detail = "CLI 直连就绪";
            return true;
        }

        /// <summary>
        /// 给 CLI 会话的完整指令：告诉它用哪张图、出几秒、存到哪，
        /// 以及项目级无水印硬规则（先生成、再局部修补、修补后复查）。
        /// </summary>
        public static string BuildPrompt(string image, int seconds, string target)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("用 VideoGen 工具做图生视频，把这张图变成一段轻微动态的视频。\r\n");
            sb.Append("源图（作为首帧）：").Append(image).Append("\r\n");
            sb.Append("时长：").Append(seconds).Append(" 秒（平台最短 4 秒就出 4 秒）。\r\n");
            sb.Append("要求：严格保持源图画面、人物、构图、光照不变；只加极轻微自然飘动")
              .Append("（发丝、衣角、呼吸感）；镜头完全静止；首尾帧一致可循环。\r\n");
            sb.Append("【无水印硬规则】").Append(CloudGen.NoWatermarkRuleCn).Append("\r\n");
            sb.Append("落地：把成片保存为 ").Append(target).Append("（父目录不存在就建），")
              .Append("确认文件存在且大于 1KB 后，用一句话报告该文件路径与大小。\r\n");
            sb.Append("注意：平台会在左上角打「AI生成」合规角标，提示词去不掉；")
              .Append("所以出片后必须抽帧检查，按项目约定用 delogo 局部修补角标区域，")
              .Append("修补后再抽帧复查，确认没有文字残留才写入目标路径。\r\n");
            sb.Append("不要修改 D:\\Wallpaper\\index.json，不要动其它图片或视频。");
            return sb.ToString();
        }

        /// <summary>
        /// 起一个 CLI 会话去出片。返回 false 时 detail 说明原因（此时不扣积分、不留假状态）。
        /// 真正的产物判定交给已有的 videos\ 目录扫描，这里不假装成功。
        /// </summary>
        public static bool TryStart(AppConfig cfg, WallpaperItem it, string image, int seconds,
                                    string target, out string detail)
        {
            StartCount++;
            LastDetail = "";
            if (SelfTestForceFail.Length > 0)
            {
                detail = SelfTestForceFail;
                LastDetail = detail;
                return false;
            }
            if (SelfTestNoStart)
            {
                // 自检专用：判定走到了直连分支就够了，绝不真起 node（真起会消耗积分）。
                detail = "自检：跳过真实启动";
                LastDetail = detail;
                return false;
            }
            string why;
            if (!Available(cfg, out why))
            {
                detail = why;
                LastDetail = why;
                return false;
            }
            if (!File.Exists(image))
            {
                detail = "源图不存在：" + image;
                LastDetail = detail;
                return false;
            }

            string script = ScriptPath(cfg);
            string prompt = BuildPrompt(image, seconds, target);

            try
            {
                ProcessStartInfo psi = CreatePsi(script, "-y --output-format json");

                Process p = new Process();
                p.StartInfo = psi;

                // CLI 未授权时会在半秒内直接退出并打印 "Authentication required"。
                // 所以启动后先盯 6 秒输出：读到这句就如实判「还没授权」，
                // 绝不因为进程起来了就报「正在出片」（那又是一次假状态）。
                StringBuilder head = new StringBuilder();
                object gate = new object();
                DataReceivedEventHandler collect = delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (gate) { if (head.Length < 8000) head.AppendLine(e.Data); }
                };
                p.OutputDataReceived += collect;
                p.ErrorDataReceived += collect;

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                // 出片指令走 stdin：BuildPrompt 里含换行和中文，
                // 塞进命令行会被 Windows 的引号/换行规则截断，CLI 只能收到半句。
                WriteStdin(p, prompt);

                DateTime t0 = DateTime.Now;
                while ((DateTime.Now - t0).TotalSeconds < 6.0)
                {
                    if (p.HasExited) break;
                    System.Threading.Thread.Sleep(150);
                }

                string seen;
                lock (gate) { seen = head.ToString(); }

                if (seen.IndexOf("Authentication required", StringComparison.OrdinalIgnoreCase) >= 0
                    || seen.IndexOf("Please use /login", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try { if (!p.HasExited) p.Kill(); } catch { }
                    detail = "WorkBuddy CLI 还没授权：需要一次性登录（设置里点「授权出片」，或在命令行执行 /login）";
                    LastDetail = detail;
                    Log.Note("WorkBuddy CLI 直连：未授权（" + it.Id + "）——" + Trim(seen, 300));
                    return false;
                }

                if (p.HasExited && seen.Length > 0)
                {
                    detail = "CLI 会话启动后立即退出：" + Trim(seen, 300);
                    LastDetail = detail;
                    Log.Note("!! WorkBuddy CLI 直连异常退出（" + it.Id + "）：" + Trim(seen, 500));
                    return false;
                }

                detail = "已启动 WorkBuddy CLI 会话（PID " + p.Id + "），正在出片";
                LastDetail = detail;
                Log.Note("WorkBuddy CLI 直连：" + it.Id + " 直接出片，PID=" + p.Id);
                try
                {
                    File.AppendAllText(LogPath,
                        "=== " + it.Id + " " + DateTime.Now + " ===\r\n" + seen, Encoding.UTF8);
                }
                catch { }
                return true;
            }
            catch (Exception ex)
            {
                detail = "启动 CLI 失败：" + ex.Message;
                LastDetail = detail;
                Log.Note("!! WorkBuddy CLI 直连启动失败：" + ex.Message);
                return false;
            }
        }

        static string Quote(string s)
        {
            return "\"" + (s == null ? "" : s.Replace("\"", "\\\"")) + "\"";
        }

        /// <summary>
        /// 统一构造启动 CLI 的进程配置。三个坑一次绕掉：
        ///
        /// 1) .NET Framework 的 ProcessStartInfo.EnvironmentVariables 是大小写不敏感的
        ///    StringDictionary，本机环境里 HTTPS_PROXY 与 https_proxy 同时存在，
        ///    一访问索引器就抛「已添加项。字典中的关键字…」。所以**完全不碰**那个字典，
        ///    改成落一个临时 .bat 由 cmd 自己 set 环境变量（cmd 内联 /c "…" 的引号嵌套
        ///    太脆，实测会把 node 路径的引号吃掉，落文件最稳）。
        /// 2) 客户端这份安装只有 dist\codebuddy-lite-wb.mjs，必须设
        ///    CODEBUDDY_FORCE_LITE_WB_BUNDLE=1 走 lite-wb 分流；不设就会依次去找
        ///    dist\codebuddy-headless 与 dist\codebuddy（两个都不存在），最后报
        ///    "Cannot find module '../dist/codebuddy'"。
        /// 3) 不绕代理会在 setProxy 里抛 "Right-hand side of 'instanceof' is not callable"。
        ///
        /// 指令一律走 stdin（CLI 的 -p 不带参数时读 stdin），bat 里只留短参数。
        /// </summary>
        static ProcessStartInfo CreatePsi(string script, string extraArgs)
        {
            string bat = Path.Combine(Path.GetTempPath(), "wb-cli-run.bat");
            StringBuilder sb = new StringBuilder();
            sb.Append("@echo off\r\n");
            sb.Append("set CODEBUDDY_FORCE_LITE_WB_BUNDLE=1\r\n");
            sb.Append("set NO_PROXY=*\r\n");
            sb.Append("\"");
            sb.Append(NodeExe());
            sb.Append("\" \"");
            sb.Append(script);
            sb.Append("\" -p ").Append(extraArgs).Append("\r\n");
            File.WriteAllText(bat, sb.ToString(), new UTF8Encoding(false));

            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Path.GetTempPath();
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            return psi;
        }

        /// <summary>
        /// 把指令写进 CLI 的 stdin。.NET Framework 没有 StandardInputEncoding，
        /// 默认编码跟控制台代码页走（本机 GBK），中文会乱码 —— 所以自己包一个 UTF-8 写入器。
        /// </summary>
        static void WriteStdin(Process p, string text)
        {
            try
            {
                using (StreamWriter w = new StreamWriter(p.StandardInput.BaseStream,
                                                         new UTF8Encoding(false)))
                {
                    w.Write(text);
                    w.Flush();
                }
            }
            catch { }
        }

        static string Trim(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        /// <summary>
        /// 交互界面（TUI）这份客户端到底有没有。11-15 22:25 查清：
        /// WorkBuddy 安装目录里 cli\dist 只有 codebuddy-lite-wb.mjs（无界面精简版），
        /// 而 TUI 走的是 require('../dist/codebuddy')，那份文件根本不存在
        /// （app.asar 里也只打包了 lite-wb 这一份，整个安装没有完整版）。
        /// 所以「在窗口里输 /login」这条路在这台机器上不可能成，必须提前说清楚，
        /// 不能把主人引到一个注定报 Cannot find module 的窗口。
        /// </summary>
        public static bool TuiAvailable(AppConfig cfg, out string detail)
        {
            detail = "";
            string root = ClientRoot(cfg);
            if (root.Length == 0) { detail = "找不到 WorkBuddy 安装目录"; return false; }
            string dist = Path.Combine(root, @"resources\app.asar.unpacked\cli\dist");
            if (!Directory.Exists(dist)) { detail = "找不到 CLI 的 dist 目录：" + dist; return false; }
            string[] names = { "codebuddy.js", "codebuddy.cjs", "codebuddy", "codebuddy-headless.js" };
            for (int i = 0; i < names.Length; i++)
                if (File.Exists(Path.Combine(dist, names[i]))) return true;
            detail = "客户端这份 CLI 只带了无界面精简版（dist\\codebuddy-lite-wb.mjs），"
                   + "缺少交互界面用的 dist\\codebuddy.js，所以在窗口里执行 /login 走不通。";
            return false;
        }

        /// <summary>
        /// 让 CLI 自己做一次授权：开一个真实终端（TUI 需要 TTY，管道喂不进去）。
        /// 主人在里面输入 /login，浏览器点一下授权，之后永久有效。
        /// </summary>
        public static bool OpenLoginConsole(AppConfig cfg, out string detail)
        {
            string tuiWhy;
            if (!TuiAvailable(cfg, out tuiWhy)) { detail = tuiWhy; return false; }
            string script = ScriptPath(cfg);
            if (script.Length == 0) { detail = "找不到 codebuddy CLI"; return false; }
            string node = NodeExe();
            try
            {
                // 用 .bat 落盘再拉起来：cmd 的引号嵌套太脆，写文件更稳，
                // 顺便把 NO_PROXY 一起设好（否则 /login 前也会崩在 setProxy）。
                //
                // 11-15 21:40 修正：之前 bat 用 Encoding.Default(GBK) 写、却又 chcp 65001，
                // 窗口里的中文提示全成乱码，主人看到一片乱码以为「命令行报错」。
                // 现在 bat 内容纯 ASCII（英文提示），文件按 UTF-8 无 BOM 落盘，
                // 与 chcp 65001 完全一致，不会再出现乱码。
                string bat = Path.Combine(Path.GetTempPath(), "wb-cli-login.bat");
                StringBuilder sb = new StringBuilder();
                sb.Append("@echo off\r\n");
                sb.Append("chcp 65001 >nul\r\n");
                sb.Append("set NO_PROXY=*\r\n");
                sb.Append("set no_proxy=*\r\n");
                // 客户端这份安装只有 dist\\codebuddy-lite-wb.mjs；不设分流会去找
                // dist\\codebuddy-headless 和 dist\\codebuddy（都不存在），
                // 窗口里就只剩 "Cannot find module '../dist/codebuddy'"。
                sb.Append("set CODEBUDDY_FORCE_LITE_WB_BUNDLE=1\r\n");
                sb.Append("title WorkBuddy CLI auth - type /login and press Enter\r\n");
                sb.Append("echo ============================================\r\n");
                sb.Append("echo  First run needs one-time authorization:\r\n");
                sb.Append("echo    1) type  /login  here and press Enter\r\n");
                sb.Append("echo    2) click Authorize in the browser\r\n");
                sb.Append("echo    3) when it says logged in, close this window\r\n");
                sb.Append("echo  If the screen looks like an agent UI, just type /login there.\r\n");
                sb.Append("echo ============================================\r\n");
                sb.Append("echo.\r\n");
                // 先把 node 与 CLI 各自单独跑一次：任何一步失败都会把原文留在窗口里，
                // 不会再出现「窗口一闪而过、只知道报错不知道报什么」。
                sb.Append("echo [1/3] node   : ").Append(node).Append("\r\n");
                sb.Append("\"").Append(node).Append("\" --version\r\n");
                sb.Append("if errorlevel 1 echo !! node failed to run - see the error above.\r\n");
                sb.Append("echo [2/3] CLI    : ").Append(script).Append("\r\n");
                sb.Append("\"").Append(node).Append("\" \"").Append(script).Append("\" --version\r\n");
                sb.Append("if errorlevel 1 echo !! CLI failed to load - see the error above.\r\n");
                sb.Append("echo [3/3] starting interactive CLI (type /login when it is ready)...\r\n");
                sb.Append("echo.\r\n");
                sb.Append("\"").Append(node).Append("\" \"").Append(script).Append("\"\r\n");
                sb.Append("echo.\r\n");
                sb.Append("echo CLI exited with code %errorlevel%.\r\n");
                // UTF-8 无 BOM：与 chcp 65001 对齐；纯 ASCII 内容其实两种编码都一样，
                // 但显式写死省得以后又有人往里塞中文。
                File.WriteAllText(bat, sb.ToString(), new UTF8Encoding(false));

                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/k \"" + bat + "\"");
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Path.GetTempPath();
                Process.Start(psi);
                detail = "已打开授权窗口：在里面输入 /login 回车，浏览器点一下授权";
                return true;
            }
            catch (Exception ex)
            {
                detail = "打开授权窗口失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 真跑一次 CLI 问一句话，用来判定「到底授权了没有」。
        /// 之前设置里「检查通道」只看文件在不在，永远说就绪，主人白点一轮。
        /// 探测用免费模型 hy4free，只花一次极短问答，不动出片额度。
        /// </summary>
        public static bool ProbeAuth(AppConfig cfg, out string detail)
        {
            detail = "";
            string why;
            if (!Available(cfg, out why)) { detail = why; return false; }
            try
            {
                ProcessStartInfo psi = CreatePsi(ScriptPath(cfg),
                                                 "--model hy4free --output-format text");

                // 把真正的命令行记下来：出问题时第一眼能看到程序是怎么调它的。
                Log.Note("CLI 探测命令行：" + psi.FileName + " " + psi.Arguments);

                using (Process p = Process.Start(psi))
                {
                    WriteStdin(p, "reply with the single word pong");
                    string outText = p.StandardOutput.ReadToEnd();
                    string errText = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(60000))
                    {
                        try { p.Kill(); } catch { }
                        detail = "CLI 探测超时（60 秒还没返回），可能是网络或代理问题";
                        Log.Note("!! WorkBuddy CLI 授权探测超时");
                        return false;
                    }
                    string all = (outText + "\n" + errText).Trim();
                    Log.Note("CLI 探测输出（退出码 " + p.ExitCode + "）：" + Trim(all, 800));

                    // 11-15 22:10 修：以前只要「输出非空且不含 Authentication required」
                    // 就判已授权，于是 node 报 "Cannot find module '../dist/codebuddy'"
                    // 也被当成「已授权」，主人白高兴一场。现在先揪错误，再要求真拿到回复。
                    if (all.IndexOf("Cannot find module", StringComparison.OrdinalIgnoreCase) >= 0
                        || all.IndexOf("MODULE_NOT_FOUND", StringComparison.OrdinalIgnoreCase) >= 0
                        || all.IndexOf("ERR_MODULE_NOT_FOUND", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        detail = "CLI 入口加载失败（不是授权问题）：" + Trim(all, 300) + "\r\n"
                               + "说明客户端这份 CLI 的 bundle 与入口不匹配，需要改用它真正带的那份。";
                        Log.Note("!! WorkBuddy CLI 入口加载失败：" + Trim(all, 500));
                        return false;
                    }
                    if (all.IndexOf("Authentication required", StringComparison.OrdinalIgnoreCase) >= 0
                        || all.IndexOf("Please use /login", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        detail = "CLI 还没授权（原话：" + Trim(all, 200) + "）\r\n"
                               + "点「授权出片（首次）」，在弹出窗口里输入 /login 回车。";
                        Log.Note("WorkBuddy CLI 授权探测：未授权");
                        return false;
                    }
                    if (all.Length == 0)
                    {
                        detail = "CLI 没有任何输出（退出码 " + p.ExitCode + "），通道异常";
                        Log.Note("!! WorkBuddy CLI 授权探测空输出，退出码 " + p.ExitCode);
                        return false;
                    }
                    // 真拿到模型回复才算通：要它答 pong，答别的（报错栈、usage、空壳）一律不算。
                    if (all.IndexOf("pong", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        detail = "CLI 有输出但没有拿到模型回复，通道异常：" + Trim(all, 300);
                        Log.Note("!! WorkBuddy CLI 探测输出里没有模型回复");
                        return false;
                    }
                    detail = "已授权，CLI 可用。模型回复：" + Trim(all, 120);
                    Log.Note("WorkBuddy CLI 授权探测：已授权（拿到 pong）");
                    return true;
                }
            }
            catch (Exception ex)
            {
                detail = "探测失败：" + ex.Message;
                Log.Note("!! WorkBuddy CLI 授权探测异常：" + ex.Message);
                return false;
            }
        }
    }
}
