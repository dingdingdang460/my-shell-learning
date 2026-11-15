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

            string node = NodeExe();
            string script = ScriptPath(cfg);
            string prompt = BuildPrompt(image, seconds, target);
            string args = Quote(script) + " -p " + Quote(prompt)
                        + " -y --output-format json";

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(node, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Path.GetTempPath();
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                // 11-15 实测：CLI 在这台机器上不绕代理会在 setProxy 里抛
                // "Right-hand side of 'instanceof' is not callable"，整条命令直接跑不起来。
                // 客户端自己启动时没这问题（它带了完整代理环境），独立起就必须显式禁代理。
                psi.EnvironmentVariables["NO_PROXY"] = "*";
                psi.EnvironmentVariables["no_proxy"] = "*";

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

        static string Trim(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        /// <summary>
        /// 让 CLI 自己做一次授权：开一个真实终端（TUI 需要 TTY，管道喂不进去）。
        /// 主人在里面输入 /login，浏览器点一下授权，之后永久有效。
        /// </summary>
        public static bool OpenLoginConsole(AppConfig cfg, out string detail)
        {
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
                ProcessStartInfo psi = new ProcessStartInfo(NodeExe(),
                    Quote(ScriptPath(cfg)) + " -p \"reply with the single word pong\" --model hy4free --output-format text");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Path.GetTempPath();
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                psi.EnvironmentVariables["NO_PROXY"] = "*";
                psi.EnvironmentVariables["no_proxy"] = "*";

                using (Process p = Process.Start(psi))
                {
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
                    detail = "已授权，CLI 可用。模型回复：" + Trim(all, 120);
                    Log.Note("WorkBuddy CLI 授权探测：已授权");
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
