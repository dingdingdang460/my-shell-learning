using System;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;

namespace WallpaperManager
{
    /// <summary>
    /// 11-14 补的两个自检，对应交接文档第 9 节的前两项待办：
    ///   1) 「导出配置…/导入配置…」按钮以前只验过 WriteTo/LoadFrom 往返，
    ///      真实点击会弹系统文件对话框，从没点过 —— 这里用 RaiseEvent 真点按钮；
    ///   2) 代理以前只验了 URL 解析，没在真实网络栈里跑过 —— 这里起本机 TCP 代理。
    /// 主体放这里，Program.cs 只负责分发，模式与 --cloudcfgtest 一致。
    /// </summary>
    public static class SelfTestExtra
    {
        static void Hit(StringBuilder sb, ref int pass, ref int total,
                        string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            sb.Append(ok ? "√ " : "× ").Append(name);
            if (!string.IsNullOrEmpty(detail)) sb.Append("  ").Append(detail);
            sb.Append("\r\n");
        }

        static string Finish(StringBuilder sb, int pass, int total)
        {
            sb.Append("合计 ").Append(pass).Append("/").Append(total);
            if (pass != total) sb.Append("  FAIL");
            return sb.ToString();
        }

        static void Put(string lib, string rel, byte[] bytes)
        {
            string p = Path.Combine(lib, rel);
            string d = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
            File.WriteAllBytes(p, bytes);
        }

        /// <summary>每种摆法之间把 videos\ 和图片目录里的视频清干净，避免上一轮残留影响下一轮。</summary>
        static void CleanVideos(string lib, string id)
        {
            string vdir = Path.Combine(lib, "videos");
            try { if (Directory.Exists(vdir)) Directory.Delete(vdir, true); }
            catch { }
            try { Directory.CreateDirectory(vdir); }
            catch { }
            try
            {
                string idir = Path.Combine(lib, "images");
                if (Directory.Exists(idir))
                {
                    string[] fs = Directory.GetFiles(idir, "*");
                    for (int i = 0; i < fs.Length; i++)
                    {
                        string n = Path.GetFileName(fs[i]).ToLowerInvariant();
                        if (n.IndexOf(".mp4", StringComparison.Ordinal) >= 0
                            || n.EndsWith(".mov", StringComparison.Ordinal)
                            || n.EndsWith(".m4v", StringComparison.Ordinal))
                            File.Delete(fs[i]);
                    }
                }
            }
            catch { }
            try { string j = CloudGen.JobPath(id); if (File.Exists(j)) File.Delete(j); }
            catch { }
            RelatedVideo.Invalidate();
        }

        static bool SameBytes(string path, byte[] sample)
        {
            try
            {
                byte[] b = File.ReadAllBytes(path);
                if (b.Length != sample.Length) return false;
                for (int i = 0; i < b.Length; i++) if (b[i] != sample[i]) return false;
                return true;
            }
            catch { return false; }
        }

        static string ModeOf(string state)
        {
            if (string.IsNullOrEmpty(state)) return "";
            int i = state.IndexOf('｜');
            return i < 0 ? state : state.Substring(0, i);
        }

        /// <summary>
        /// 摆出一种「相关视频存在」的样子，把所有能走到下单的入口都点一遍，
        /// 逐项断言：认得出来、归一到规范路径、零工单、零 HTTP 提交、不弹确认浮条。
        /// </summary>
        static void CheckOne(StringBuilder sb, ref int pass, ref int total,
                             App app, WallpaperItem probe, string lib, byte[] sample,
                             string label, string relPath, Action setup,
                             MockCloudServer mock, WallpaperManager.MainWindow mw)
        {
            CleanVideos(lib, probe.Id);
            if (!string.IsNullOrEmpty(relPath)) Put(lib, relPath, sample);
            if (setup != null) setup();
            RelatedVideo.Invalidate();

            int jobsBefore = CloudGen.PendingCount();
            int submitsBefore = mock.SubmitCount;
            string modeBefore = mw == null ? "" : ModeOf(mw.SelfTestCloudState());

            bool has = app.HasCloudVideo(probe);
            string canonical = CloudGen.VideoPath(lib, probe.Id);
            bool made = File.Exists(canonical);
            bool same = made && SameBytes(canonical, sample);

            string m1, m2;
            bool q = app.QueueCloudGen(probe, probe.ImagePath, out m1);
            bool e = CloudGen.Enqueue(app.Config, probe, probe.ImagePath, out m2);
            app.GenCloud(probe);
            string modeAfter = mw == null ? "" : ModeOf(mw.SelfTestCloudState());

            Hit(sb, ref pass, ref total, label + "：认出相关视频", has, "");
            Hit(sb, ref pass, ref total, "  └ 归一到规范主文件且内容一致", made && same,
                made ? new FileInfo(canonical).Length + " 字节" : "未生成");
            Hit(sb, ref pass, ref total, "  └ 三个下单入口全部拒绝", !q && !e,
                "队列=" + q + " 工单=" + e + " " + m1);
            Hit(sb, ref pass, ref total, "  └ 工单队列没有被写入",
                CloudGen.PendingCount() == jobsBefore && !File.Exists(CloudGen.JobPath(probe.Id)),
                "队列数=" + CloudGen.PendingCount());
            Hit(sb, ref pass, ref total, "  └ 模拟服务端零提交（没花钱）",
                mock.SubmitCount == submitsBefore, "提交数=" + mock.SubmitCount);
            Hit(sb, ref pass, ref total, "  └ 没有弹确认下单浮条",
                string.Equals(modeBefore, modeAfter, StringComparison.Ordinal),
                modeBefore + " → " + modeAfter);
            Hit(sb, ref pass, ref total, "  └ 状态机报「已就绪」",
                CloudGen.StateOf(app.Config, probe) == CloudGen.State.Ready, "");
            RelatedVideo.Invalidate();
        }

        /// <summary>
        /// 11-14：验证「只要关联文件夹里有相关视频就该播，绝不重新生成浪费钱」。
        ///
        /// 隔离：LibRoot / ConfigStore.DirOverride 都指向临时目录，真实壁纸库、
        /// config.json、工单队列一个字节都不碰；SelfTestNoPlay 保证不会换掉主人的桌面。
        /// 反证：故意把服务商和假 Key 配好、把 GenApi 基址指到本机模拟服务端，
        /// 这样只要有任何一条下单路径漏了闸，RunApiJob 就会真的 POST 一次，
        /// mock.SubmitCount 立刻变正 —— 断言它始终是 0 就是「零费用」的硬证据。
        /// </summary>
        public static string RelatedVideoGuard(App app)
        {
            StringBuilder sb = new StringBuilder();
            int pass = 0, total = 0;
            string savedRoot = app.Config.LibRoot;
            string savedDirOverride = ConfigStore.DirOverride;
            string savedProvider = app.Config.GenProvider;
            string savedKey = app.Config.GenApiKey;
            string savedPending = app.Config.PendingCloudId;
            string savedEndpoint = GenApi.EndpointOverride;
            string realIndex = Path.Combine(savedRoot, "index.json");
            string realIndexBefore = "";
            try { if (File.Exists(realIndex)) realIndexBefore = File.ReadAllText(realIndex, Encoding.UTF8); }
            catch { }
            int realJobsBefore = CloudGen.PendingCount();

            string tmp = Path.Combine(Path.GetTempPath(),
                                      "wallpaper-relvid-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string lib = Path.Combine(tmp, "lib");
            MockCloudServer mock = new MockCloudServer();
            WallpaperManager.MainWindow mw = app.MainWindow as WallpaperManager.MainWindow;

            try
            {
                Directory.CreateDirectory(Path.Combine(lib, "images"));
                Directory.CreateDirectory(Path.Combine(lib, "videos"));
                Directory.CreateDirectory(Path.Combine(lib, "thumbs"));
                Directory.CreateDirectory(Path.Combine(tmp, "cfg"));
                string clip = Path.Combine(tmp, "clip.mp4");
                File.WriteAllBytes(clip, Encoding.UTF8.GetBytes("mock clip for submit counter"));
                bool started = mock.Start(clip);
                Hit(sb, ref pass, ref total, "本机模拟服务端启动（零外部流量）", started,
                    started ? mock.BaseUrl : mock.LastError);
                if (!started) return Finish(sb, pass, total);

                ConfigStore.DirOverride = Path.Combine(tmp, "cfg");
                app.Config.LibRoot = lib;
                app.Config.GenProvider = "wanx";
                app.Config.GenApiKey = "MOCK-KEY-NO-SPEND";
                app.Config.PendingCloudId = "";
                GenApi.EndpointOverride = mock.BaseUrl;
                App.SelfTestNoPlay = true;
                CloudGen.EnsureDirs();

                WallpaperItem probe = new WallpaperItem();
                probe.Id = "zz-relvid-probe";
                probe.Title = "相关视频自检";
                probe.File = "images/" + probe.Id + ".jpg";
                probe.Thumb = "thumbs/" + probe.Id + ".jpg";
                probe.ImagePath = Path.Combine(lib, "images", probe.Id + ".jpg");
                probe.ThumbAbs = Path.Combine(lib, "thumbs", probe.Id + ".jpg");
                File.WriteAllBytes(probe.ImagePath, Encoding.UTF8.GetBytes("probe image"));
                File.WriteAllBytes(probe.ThumbAbs, Encoding.UTF8.GetBytes("probe thumb"));

                byte[] sample = Encoding.UTF8.GetBytes("wallpaper-related-video-probe");
                string[] labels = new string[] {
                    "规范主文件 videos\\<id>.mp4",
                    "旧片备份 <id>.mp4.bak-v2",
                    "变体命名 <id>.raw.mp4",
                    "关联子文件夹 videos\\_raw\\<id>.raw.mp4",
                    "图片同目录 images\\<id>.mp4",
                    "以图命名的文件夹 videos\\<id>\\clip.mp4"
                };
                string[] paths = new string[] {
                    Path.Combine("videos", probe.Id + ".mp4"),
                    Path.Combine("videos", probe.Id + ".mp4.bak-v2"),
                    Path.Combine("videos", probe.Id + ".raw.mp4"),
                    Path.Combine("videos", "_raw", probe.Id + ".raw.mp4"),
                    Path.Combine("images", probe.Id + ".mp4"),
                    Path.Combine("videos", probe.Id, "clip.mp4")
                };
                for (int i = 0; i < paths.Length; i++)
                    CheckOne(sb, ref pass, ref total, app, probe, lib, sample,
                             labels[i], paths[i], null, mock, mw);

                // ---- 工单 out 指向库内但改过名字的片子：老版本 / 外部生成器也算 ----
                string outside = Path.Combine(lib, "videos", "legacy-output.mp4");
                Action jobSetup = delegate
                {
                    File.WriteAllBytes(outside, sample);
                    string job = Path.Combine(CloudGen.DoneDir, probe.Id + ".json");
                    StringBuilder j = new StringBuilder();
                    j.Append("{\r\n  \"id\": ").Append(Json.Quote(probe.Id)).Append(",\r\n");
                    j.Append("  \"image\": ").Append(Json.Quote(probe.ImagePath)).Append(",\r\n");
                    j.Append("  \"out\": ").Append(Json.Quote(outside)).Append("\r\n}\r\n");
                    File.WriteAllText(job, j.ToString(), new UTF8Encoding(false));
                };
                CheckOne(sb, ref pass, ref total, app, probe, lib, sample,
                         "工单 out 指向库内改名的片子", null, jobSetup, mock, mw);

                // ---- 反例：完全不相干的视频 / 同前缀别的 id，绝不能误认 ----
                CleanVideos(lib, probe.Id);
                Put(lib, Path.Combine("videos", "totally-unrelated.mp4"), sample);
                Put(lib, Path.Combine("videos", probe.Id + "0.mp4"), sample);
                RelatedVideo.Invalidate();
                Hit(sb, ref pass, ref total, "无关视频 / 同前缀别的 id 不误认",
                    !app.HasCloudVideo(probe), "");
                string negMsg;
                bool allow = CloudGen.Enqueue(app.Config, probe, probe.ImagePath, out negMsg);
                Hit(sb, ref pass, ref total, "没有任何相关视频时才允许下单",
                    allow && File.Exists(CloudGen.JobPath(probe.Id)), negMsg);
                try { if (File.Exists(CloudGen.JobPath(probe.Id))) File.Delete(CloudGen.JobPath(probe.Id)); }
                catch { }

                Hit(sb, ref pass, ref total, "全程模拟服务端零提交（没花一分钱）",
                    mock.SubmitCount == 0, "提交数=" + mock.SubmitCount);
            }
            catch (Exception ex)
            {
                Hit(sb, ref pass, ref total, "自检过程无异常", false,
                    ex.GetType().Name + "：" + ex.Message);
            }
            finally
            {
                App.SelfTestNoPlay = false;
                app.Config.LibRoot = savedRoot;
                app.Config.GenProvider = savedProvider;
                app.Config.GenApiKey = savedKey;
                app.Config.PendingCloudId = savedPending;
                ConfigStore.DirOverride = savedDirOverride;
                RelatedVideo.Invalidate();
                try { mock.Stop(); }
                catch { }
                GenApi.EndpointOverride = savedEndpoint;
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
                catch { }
            }

            string realIndexAfter = "";
            try { if (File.Exists(realIndex)) realIndexAfter = File.ReadAllText(realIndex, Encoding.UTF8); }
            catch { }
            Hit(sb, ref pass, ref total, "真实库 index.json 未被改动",
                string.Equals(realIndexBefore, realIndexAfter, StringComparison.Ordinal), "");
            Hit(sb, ref pass, ref total, "真实工单队列数量不变",
                CloudGen.PendingCount() == realJobsBefore, "队列数=" + realJobsBefore);
            Hit(sb, ref pass, ref total, "LibRoot / 服务商 / Key / 端点已还原",
                string.Equals(app.Config.LibRoot, savedRoot, StringComparison.OrdinalIgnoreCase)
                && string.Equals(app.Config.GenProvider, savedProvider, StringComparison.Ordinal)
                && string.Equals(app.Config.GenApiKey, savedKey, StringComparison.Ordinal)
                && string.Equals(GenApi.EndpointOverride, savedEndpoint, StringComparison.Ordinal)
                && !App.SelfTestNoPlay, app.Config.LibRoot);
            return Finish(sb, pass, total);
        }

        /// <summary>
        /// 飘动视频备份自愈：主文件被移走、只剩 .bak/.bak-v2 时，应用应自己恢复，
        /// 而不是让主人再花一次额度去生成。全程用临时库目录，真实壁纸库不受影响。
        /// </summary>
        public static string VideoRecovery(App app)
        {
            StringBuilder sb = new StringBuilder();
            int pass = 0, total = 0;
            string savedRoot = app.Config.LibRoot;
            string tmp = Path.Combine(Path.GetTempPath(),
                                      "wallpaper-vid-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                WallpaperItem probe = null;
                for (int i = 0; i < app.Items.Count; i++)
                    if (!app.Items[i].IsVideo && !string.IsNullOrEmpty(app.Items[i].Id))
                    { probe = app.Items[i]; break; }
                if (probe == null)
                {
                    Hit(sb, ref pass, ref total, "库里有可测的静态图条目", false, "");
                    return Finish(sb, pass, total);
                }
                string vdir = Path.Combine(tmp, "videos");
                Directory.CreateDirectory(vdir);
                string main = Path.Combine(vdir, probe.Id + ".mp4");
                string bak = Path.Combine(vdir, probe.Id + ".mp4.bak-v2");
                byte[] sample = Encoding.UTF8.GetBytes("wallpaper-video-recovery-probe");
                File.WriteAllBytes(bak, sample);
                app.Config.LibRoot = tmp;

                Hit(sb, ref pass, ref total, "造场景：主文件原本不存在",
                    !File.Exists(main), probe.Id);
                bool got = app.HasCloudVideo(probe);
                Hit(sb, ref pass, ref total, "有备份时能认出飘动视频", got, "");
                bool made = File.Exists(main);
                bool same = false;
                try
                {
                    byte[] back = File.ReadAllBytes(main);
                    same = back.Length == sample.Length;
                    if (same)
                        for (int i = 0; i < back.Length; i++)
                            if (back[i] != sample[i]) { same = false; break; }
                }
                catch { }
                Hit(sb, ref pass, ref total, "主文件按备份内容恢复", made && same,
                    made ? new FileInfo(main).Length + " 字节" : "未生成");
                Hit(sb, ref pass, ref total, "备份文件仍保留", File.Exists(bak), "");

                DateTime stamp = made ? File.GetLastWriteTimeUtc(main) : DateTime.MinValue;
                app.HasCloudVideo(probe);
                Hit(sb, ref pass, ref total, "重复查询不会重复恢复",
                    File.Exists(main) && File.GetLastWriteTimeUtc(main) == stamp, "");

                File.Delete(main);
                File.Delete(bak);
                RelatedVideo.Invalidate();      // 目录没变但文件删了，缓存要失效
                Hit(sb, ref pass, ref total, "无备份时保持「需要生成」",
                    !app.HasCloudVideo(probe) && !File.Exists(main), "");
            }
            catch (Exception ex)
            {
                Hit(sb, ref pass, ref total, "自检过程无异常", false,
                    ex.GetType().Name + "：" + ex.Message);
            }
            finally
            {
                app.Config.LibRoot = savedRoot;
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
            Hit(sb, ref pass, ref total, "真实库目录已还原",
                string.Equals(app.Config.LibRoot, savedRoot, StringComparison.OrdinalIgnoreCase),
                app.Config.LibRoot);
            return Finish(sb, pass, total);
        }
        /// <summary>
        /// 真点「导出配置…」「导入配置…」，把整条链路走完：
        /// 落盘 → 覆盖前留 .bak → 新配置重新读回 → Net.Configure 同步 → 界面刷新。
        /// 隔离：ConfigStore.DirOverride 把 config.json 挪到临时目录，真实配置一个字节都不碰。
        /// </summary>
        public static string ImportExport(App app)
        {
            StringBuilder sb = new StringBuilder();
            int pass = 0, total = 0;
            string savedDir = ConfigStore.DirOverride;
            AppConfig savedConfig = app.Config;
            string realPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "wallpaper", "config.json");
            string realBefore = "";
            try { if (File.Exists(realPath)) realBefore = File.ReadAllText(realPath, Encoding.UTF8); }
            catch { }

            string tmp = Path.Combine(Path.GetTempPath(),
                                      "wallpaper-ie-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            SettingsWindow sw = null;

            try
            {
                Directory.CreateDirectory(tmp);
                ConfigStore.DirOverride = tmp;
                string cfg = Path.Combine(tmp, "config.json");
                string exp = Path.Combine(tmp, "export.json");
                ConfigStore.WriteTo(savedConfig, cfg);

                sw = new SettingsWindow(app);
                SettingsWindow.SuppressDialogs = true;
                SettingsWindow.AutoAnswer = MessageBoxResult.OK;
                SettingsWindow.ExportPick = exp;
                SettingsWindow.ImportPick = exp;

                // ---- 1) 真点导出 ----
                sw.SelfTestExportClick();
                bool made = File.Exists(exp);
                AppConfig back = made ? ConfigStore.LoadFrom(exp) : new AppConfig();
                Hit(sb, ref pass, ref total, "点击导出后文件生成", made,
                    made ? new FileInfo(exp).Length + " 字节" : "文件不存在");
                Hit(sb, ref pass, ref total, "导出内容与当前配置一致",
                    made && back.IntervalMinutes == savedConfig.IntervalMinutes
                         && back.CacheLimitMb == savedConfig.CacheLimitMb
                         && string.Equals(back.LibRoot, savedConfig.LibRoot, StringComparison.OrdinalIgnoreCase),
                    "轮换=" + back.IntervalMinutes + " 缓存=" + back.CacheLimitMb);

                // ---- 2) 改成「另一台电脑的配置」再真点导入 ----
                // libRoot 保持原值：rootChanged=false，ApplySettings 不会去换桌面壁纸。
                AppConfig other = ConfigStore.LoadFrom(exp);
                other.IntervalMinutes = 45;
                other.CacheLimitMb = 456;
                other.DynamicFps = 26;
                other.Proxy = "127.0.0.1:7890";
                ConfigStore.WriteTo(other, exp);
                string beforeImport = File.ReadAllText(cfg, Encoding.UTF8);

                sw.SelfTestImportClick();
                AppConfig now = ConfigStore.LoadFrom(cfg);
                Hit(sb, ref pass, ref total, "导入后 config.json 被覆盖",
                    now.IntervalMinutes == 45 && now.CacheLimitMb == 456 && now.DynamicFps == 26,
                    "轮换=" + now.IntervalMinutes + " 缓存=" + now.CacheLimitMb + " fps=" + now.DynamicFps);
                Hit(sb, ref pass, ref total, "内存配置对象已换新",
                    app.Config.IntervalMinutes == 45 && app.Config.CacheLimitMb == 456, "");
                Hit(sb, ref pass, ref total, "代理随配置重新生效",
                    string.Equals(Net.Current, "127.0.0.1:7890", StringComparison.Ordinal), Net.Current);
                string fields = sw.SelfTestFields();
                Hit(sb, ref pass, ref total, "界面输入框已刷新",
                    fields.IndexOf("interval=45", StringComparison.Ordinal) >= 0
                 && fields.IndexOf("proxy=127.0.0.1:7890", StringComparison.Ordinal) >= 0
                 && fields.IndexOf("cache=456", StringComparison.Ordinal) >= 0, fields);
                bool bakOk = false;
                try
                {
                    bakOk = File.Exists(cfg + ".bak")
                         && string.Equals(File.ReadAllText(cfg + ".bak", Encoding.UTF8),
                                          beforeImport, StringComparison.Ordinal);
                }
                catch { }
                Hit(sb, ref pass, ref total, "覆盖前留了 .bak 备份", bakOk, "");

                // ---- 3) 点「取消」不能动配置 ----
                SettingsWindow.AutoAnswer = MessageBoxResult.Cancel;
                AppConfig cancelCfg = ConfigStore.LoadFrom(cfg);
                cancelCfg.IntervalMinutes = 99;
                ConfigStore.WriteTo(cancelCfg, exp);
                sw.SelfTestImportClick();
                Hit(sb, ref pass, ref total, "点取消不覆盖配置",
                    ConfigStore.LoadFrom(cfg).IntervalMinutes == 45 && app.Config.IntervalMinutes == 45, "");

                // ---- 4) 坏文件：原来会让 JavaScriptSerializer 抛未处理异常 ----
                SettingsWindow.AutoAnswer = MessageBoxResult.OK;
                File.WriteAllText(exp, "not a json {{{", new UTF8Encoding(false));
                sw.SelfTestImportClick();
                Hit(sb, ref pass, ref total, "坏文件不抛异常也不改配置",
                    ConfigStore.LoadFrom(cfg).IntervalMinutes == 45, "");

                // ---- 5) 合法 JSON 但不是本程序配置：原来会把设置静默清空 ----
                File.WriteAllText(exp, "{\"a\":1}", new UTF8Encoding(false));
                sw.SelfTestImportClick();
                Hit(sb, ref pass, ref total, "缺 libRoot 的 JSON 被拒绝",
                    ConfigStore.LoadFrom(cfg).IntervalMinutes == 45, "");

                string realAfter = "";
                try { if (File.Exists(realPath)) realAfter = File.ReadAllText(realPath, Encoding.UTF8); }
                catch { }
                Hit(sb, ref pass, ref total, "真实 config.json 未被触碰",
                    string.Equals(realBefore, realAfter, StringComparison.Ordinal), "");
            }
            catch (Exception ex)
            {
                Hit(sb, ref pass, ref total, "自检过程无异常", false,
                    ex.GetType().Name + "：" + ex.Message);
            }
            finally
            {
                SettingsWindow.ExportPick = null;
                SettingsWindow.ImportPick = null;
                SettingsWindow.SuppressDialogs = false;
                SettingsWindow.AutoAnswer = MessageBoxResult.OK;
                ConfigStore.DirOverride = savedDir;
                app.Config = savedConfig;
                Net.Configure(savedConfig.Proxy);
                try { app.ApplySettings(false); } catch { }   // 轮换计时器 / 界面一并回原状
                try { if (sw != null) sw.Close(); } catch { }
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
            return Finish(sb, pass, total);
        }

        /// <summary>
        /// 代理真实链路：Net.Configure → HttpWebRequest / WebClient → 本机 TCP 代理 → 响应回来。
        /// 用「请求行是不是绝对 URI」区分走没走代理，比断言「返回成功」更有说服力。
        /// 目标域名用 .invalid（不解析、不外发），全程零外部流量。
        /// </summary>
        public static string Proxy()
        {
            StringBuilder sb = new StringBuilder();
            int pass = 0, total = 0;
            string savedProxy = Net.Current;
            MockProxyServer mock = new MockProxyServer();
            string url = "http://wallpaper-proxy-check.invalid/probe";
            string resp = "", err = "";

            try
            {
                bool started = mock.Start();
                Hit(sb, ref pass, ref total, "本机代理服务启动", started,
                    started ? mock.BaseUrl : mock.LastError);
                if (!started) return Finish(sb, pass, total);

                Net.Configure("127.0.0.1:" + mock.Port);

                bool got = Http.Get(url, "", 6000, out resp, out err);
                Hit(sb, ref pass, ref total, "HttpWebRequest 经代理拿到响应",
                    got && resp.IndexOf("\"proxy\":\"ok\"", StringComparison.Ordinal) >= 0,
                    got ? resp : err);
                Hit(sb, ref pass, ref total, "代理收到绝对 URI 请求行",
                    mock.RequestCount == 1
                 && mock.LastRequestLine.StartsWith("GET http://", StringComparison.Ordinal),
                    mock.LastRequestLine);
                Hit(sb, ref pass, ref total, "Host 头指向目标域名",
                    mock.LastHost.IndexOf("wallpaper-proxy-check.invalid", StringComparison.Ordinal) >= 0,
                    mock.LastHost);

                // GenApi.Download 走 WebClient，也要确认 Net.Apply(wc) 生效
                string wcText = "", wcErr = "";
                bool wcOk = false;
                try
                {
                    using (WebClient wc = new WebClient())
                    {
                        Net.Apply(wc);
                        wcText = wc.DownloadString(url + "?wc=1");
                        wcOk = wcText.IndexOf("\"proxy\":\"ok\"", StringComparison.Ordinal) >= 0;
                    }
                }
                catch (Exception ex) { wcErr = ex.GetType().Name + "：" + ex.Message; }
                Hit(sb, ref pass, ref total, "WebClient 同样走代理",
                    wcOk && mock.RequestCount == 2, wcOk ? "请求数=" + mock.RequestCount : wcErr);

                // 本机地址必须绕过代理：--cloudflowtest 的本机模拟服务端不能受代理设置影响
                int beforeLocal = mock.RequestCount;
                string r2 = "", e2 = "";
                bool localOk = Http.Get(mock.BaseUrl + "/local", "", 4000, out r2, out e2);
                bool bypassed = mock.RequestCount == beforeLocal + 1
                             && mock.LastRequestLine.IndexOf("http://", StringComparison.Ordinal) < 0;
                Hit(sb, ref pass, ref total, "本机地址绕过代理（模拟服务端不受影响）",
                    localOk && bypassed, mock.LastRequestLine);

                // 空配置 = 直连，绝不偷偷走代理（这条是老行为的红线）
                Net.Configure("");
                int beforeDirect = mock.RequestCount;
                string r3 = "", e3 = "";
                bool directFail = !Http.Get(url, "", 4000, out r3, out e3);
                Hit(sb, ref pass, ref total, "空配置直连且代理零请求",
                    directFail && mock.RequestCount == beforeDirect, "err=" + e3);
                Hit(sb, ref pass, ref total, "直连时 Net.Proxy 为 null", Net.Proxy == null, "");
            }
            catch (Exception ex)
            {
                Hit(sb, ref pass, ref total, "自检过程无异常", false,
                    ex.GetType().Name + "：" + ex.Message);
            }
            finally
            {
                Net.Configure(savedProxy);
                mock.Stop();
            }
            Hit(sb, ref pass, ref total, "代理设置已还原",
                string.Equals(Net.Current, savedProxy, StringComparison.Ordinal), Net.Current);
            return Finish(sb, pass, total);
        }

        /// <summary>
        /// 11-14 主人追问：「点完就显示生成中，还是假的吗？」
        /// 验：没选服务商 / 没填 Key 时必须弹窗列明白缺什么，且不写工单、不报 Queued、
        /// 不改动效标记；再验 22:37 那种历史僵尸工单会被判成 Manual 而不是「生成中」。
        /// 全程隔离到临时目录，真实 config / 队列 / 壁纸库一个字节不碰。
        /// </summary>
        public static string CloudConfigGuard(App app)
        {
            StringBuilder sb = new StringBuilder();
            int pass = 0, total = 0;
            string savedRoot = app.Config.LibRoot;
            string savedDirOverride = ConfigStore.DirOverride;
            string savedProvider = app.Config.GenProvider;
            string savedKey = app.Config.GenApiKey;
            string savedModel = app.Config.GenModel;
            string savedPending = app.Config.PendingCloudId;
            bool savedNoDialogs = App.SelfTestNoDialogs;
            MessageBoxResult savedAnswer = App.SelfTestCloudAnswer;
            int dialogsBefore = App.CloudDialogCount;
            int realJobsBefore = CloudGen.PendingCount();

            string tmp = Path.Combine(Path.GetTempPath(),
                                      "wallpaper-cfgdlg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string lib = Path.Combine(tmp, "lib");
                Directory.CreateDirectory(Path.Combine(lib, "images"));
                Directory.CreateDirectory(Path.Combine(lib, "videos"));
                Directory.CreateDirectory(Path.Combine(tmp, "cfg"));

                ConfigStore.DirOverride = Path.Combine(tmp, "cfg");
                app.Config.LibRoot = lib;
                app.Config.GenProvider = "";
                app.Config.GenApiKey = "";
                app.Config.GenModel = "";
                app.Config.PendingCloudId = "";
                App.SelfTestNoDialogs = true;
                App.SelfTestCloudAnswer = MessageBoxResult.No;   // 选「先不生成」，不开设置窗口
                CloudGen.EnsureDirs();

                WallpaperItem probe = new WallpaperItem();
                probe.Id = "cfgdlg-guard-01";
                probe.Title = "假生成中自检";
                probe.File = "images/" + probe.Id + ".jpg";
                probe.ImagePath = Path.Combine(lib, "images", probe.Id + ".jpg");
                File.WriteAllBytes(probe.ImagePath, Encoding.UTF8.GetBytes("probe image"));

                string msg;
                bool queued = app.QueueCloudGen(probe, probe.ImagePath, out msg);
                Hit(sb, ref pass, ref total, "没配 API 时拒绝下单", !queued, msg);
                Hit(sb, ref pass, ref total, "没有写工单",
                    !File.Exists(CloudGen.JobPath(probe.Id)), "");
                Hit(sb, ref pass, ref total, "缺信息时确实弹窗提示",
                    App.CloudDialogCount == dialogsBefore + 1,
                    "次数 " + dialogsBefore + " → " + App.CloudDialogCount);
                Hit(sb, ref pass, ref total, "弹窗列明「服务商」",
                    App.LastCloudDialog.IndexOf("服务商") >= 0, "");
                Hit(sb, ref pass, ref total, "弹窗列明「API Key」",
                    App.LastCloudDialog.IndexOf("API Key") >= 0, "");
                Hit(sb, ref pass, ref total, "弹窗写明两家服务商与单价",
                    App.LastCloudDialog.IndexOf("通义万相") >= 0
                    && App.LastCloudDialog.IndexOf("火山方舟") >= 0, "");
                Hit(sb, ref pass, ref total, "弹窗给出工单目录",
                    App.LastCloudDialog.IndexOf("工单目录") >= 0, "");
                Hit(sb, ref pass, ref total, "状态不是「生成中」",
                    CloudGen.StateOf(app.Config, probe) == CloudGen.State.None,
                    CloudGen.StateOf(app.Config, probe).ToString());
                Hit(sb, ref pass, ref total, "动效标记没被偷偷改成动态", !probe.Motion, "");
                Hit(sb, ref pass, ref total, "状态栏明说没有下单 / 没有扣费",
                    msg.IndexOf("没有下单") >= 0 && msg.IndexOf("没有扣费") >= 0, msg);

                // 模拟 22:37 那种「没配 API 却留下的僵尸工单」：必须判 Manual，不能冒充生成中。
                string zombie = CloudGen.JobPath(probe.Id);
                File.WriteAllText(zombie,
                    "{\r\n  \"id\": " + Json.Quote(probe.Id) + ",\r\n  \"created\": 1\r\n}\r\n",
                    new UTF8Encoding(false));
                Hit(sb, ref pass, ref total, "历史僵尸工单判为 Manual（不冒充生成中）",
                    CloudGen.StateOf(app.Config, probe) == CloudGen.State.Manual,
                    CloudGen.StateOf(app.Config, probe).ToString());
                try { File.Delete(zombie); } catch { }
            }
            catch (Exception ex)
            {
                Hit(sb, ref pass, ref total, "自检过程无异常", false,
                    ex.GetType().Name + "：" + ex.Message);
            }
            finally
            {
                app.Config.LibRoot = savedRoot;
                app.Config.GenProvider = savedProvider;
                app.Config.GenApiKey = savedKey;
                app.Config.GenModel = savedModel;
                app.Config.PendingCloudId = savedPending;
                ConfigStore.DirOverride = savedDirOverride;
                App.SelfTestNoDialogs = savedNoDialogs;
                App.SelfTestCloudAnswer = savedAnswer;
                RelatedVideo.Invalidate();
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
            Hit(sb, ref pass, ref total, "真实工单队列数量不变",
                CloudGen.PendingCount() == realJobsBefore, "队列数=" + realJobsBefore);
            Hit(sb, ref pass, ref total, "LibRoot / 服务商 / Key 已还原",
                string.Equals(app.Config.LibRoot, savedRoot, StringComparison.OrdinalIgnoreCase)
                && string.Equals(app.Config.GenProvider, savedProvider, StringComparison.Ordinal)
                && string.Equals(app.Config.GenApiKey, savedKey, StringComparison.Ordinal), "");
            return Finish(sb, pass, total);
        }

        /// <summary>
        /// 11-14 主人实测：「video 文件夹下有视频啊，怎么最后一张还要生成？」
        /// 专测最后一层兜底 —— 没带 id、但名字是云端服务商原始出片（带 UTC 时间戳）的散片，
        /// 也要被认出来直接用；无关视频 / 同前缀别的 id 不能误认；没有才允许下单。
        /// 反证：故意配好假 Key 并把端点指到本机模拟服务端，漏一道闸就会真 POST 一次。
        /// 另外断言「不往库里复制/新建任何文件」—— 这一层纯读，不动主人的壁纸库。
        /// </summary>
        public static string OrphanGuard(App app)
        {
            StringBuilder sb = new StringBuilder();
            int pass = 0, total = 0;
            string savedRoot = app.Config.LibRoot;
            string savedDirOverride = ConfigStore.DirOverride;
            string savedProvider = app.Config.GenProvider;
            string savedKey = app.Config.GenApiKey;
            string savedPending = app.Config.PendingCloudId;
            string savedEndpoint = GenApi.EndpointOverride;
            bool savedNoPlay = App.SelfTestNoPlay;
            bool savedNoDialogs = App.SelfTestNoDialogs;
            int realJobsBefore = CloudGen.PendingCount();
            string realIndex = Path.Combine(savedRoot, "index.json");
            string realIndexBefore = "";
            try { if (File.Exists(realIndex)) realIndexBefore = File.ReadAllText(realIndex, Encoding.UTF8); }
            catch { }

            string tmp = Path.Combine(Path.GetTempPath(),
                                      "wallpaper-orphan-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string lib = Path.Combine(tmp, "lib");
            MockCloudServer mock = new MockCloudServer();
            try
            {
                Directory.CreateDirectory(Path.Combine(lib, "images"));
                Directory.CreateDirectory(Path.Combine(lib, "videos"));
                Directory.CreateDirectory(Path.Combine(tmp, "cfg"));
                string clip = Path.Combine(tmp, "clip.mp4");
                File.WriteAllBytes(clip, Encoding.UTF8.GetBytes("mock clip for submit counter"));
                bool started = mock.Start(clip);
                Hit(sb, ref pass, ref total, "本机模拟服务端启动（零外部流量）", started,
                    started ? mock.BaseUrl : mock.LastError);
                if (!started) return Finish(sb, pass, total);

                ConfigStore.DirOverride = Path.Combine(tmp, "cfg");
                app.Config.LibRoot = lib;
                app.Config.GenProvider = "wanx";
                app.Config.GenApiKey = "MOCK-KEY-NO-SPEND";
                app.Config.PendingCloudId = "";
                GenApi.EndpointOverride = mock.BaseUrl;
                App.SelfTestNoPlay = true;
                App.SelfTestNoDialogs = true;
                CloudGen.EnsureDirs();

                byte[] sample = Encoding.UTF8.GetBytes("wallpaper-orphan-probe");
                WallpaperItem probe = new WallpaperItem();
                probe.Id = "orphan-guard-01";
                probe.Title = "未归属出片自检";
                probe.File = "images/" + probe.Id + ".jpg";
                probe.ImagePath = Path.Combine(lib, "images", probe.Id + ".jpg");
                File.WriteAllBytes(probe.ImagePath, sample);
                Put(lib, "images/landscape.jpg", sample);      // 有主的图，别让 KnownKeys 空着

                string orphanRel = "videos/Anime_girl_waving_2026-11-12T10-02-47.mp4";
                string orphan = Path.Combine(lib, orphanRel);
                Put(lib, orphanRel, sample);
                RelatedVideo.Invalidate();

                string canonical = CloudGen.VideoPath(lib, probe.Id);
                Hit(sb, ref pass, ref total, "没带 id 的原始出片被认出来",
                    app.HasCloudVideo(probe), "");
                Hit(sb, ref pass, ref total, "状态机报 Ready（菜单不会再说要生成）",
                    CloudGen.StateOf(app.Config, probe) == CloudGen.State.Ready, "");

                string m1, m2;
                bool q = app.QueueCloudGen(probe, probe.ImagePath, out m1);
                bool e = CloudGen.Enqueue(app.Config, probe, probe.ImagePath, out m2);
                app.GenCloud(probe);
                Hit(sb, ref pass, ref total, "三个下单入口全部拒绝", !q && !e, m1);
                Hit(sb, ref pass, ref total, "没有写工单",
                    !File.Exists(CloudGen.JobPath(probe.Id)), "");
                Hit(sb, ref pass, ref total, "模拟服务端零提交（零费用）",
                    mock.SubmitCount == 0, "提交数=" + mock.SubmitCount);
                Hit(sb, ref pass, ref total, "纯读：没有复制/新建规范主文件",
                    !File.Exists(canonical) && File.Exists(orphan), "");

                // 反例：随便一个视频、或同前缀别的 id，不能被误认
                File.Delete(orphan);
                Put(lib, "videos/totally-unrelated.mp4", sample);
                Put(lib, "videos/" + probe.Id + "0.mp4", sample);
                RelatedVideo.Invalidate();
                Hit(sb, ref pass, ref total, "无关视频 / 同前缀别的 id 不误认",
                    !app.HasCloudVideo(probe), "");
                string m3;
                bool allow = CloudGen.Enqueue(app.Config, probe, probe.ImagePath, out m3);
                Hit(sb, ref pass, ref total, "没有任何相关视频时才允许下单",
                    allow && File.Exists(CloudGen.JobPath(probe.Id)), m3);
                try { if (File.Exists(CloudGen.JobPath(probe.Id))) File.Delete(CloudGen.JobPath(probe.Id)); }
                catch { }
                Hit(sb, ref pass, ref total, "全程模拟服务端零提交",
                    mock.SubmitCount == 0, "提交数=" + mock.SubmitCount);
            }
            catch (Exception ex)
            {
                Hit(sb, ref pass, ref total, "自检过程无异常", false,
                    ex.GetType().Name + "：" + ex.Message);
            }
            finally
            {
                App.SelfTestNoPlay = savedNoPlay;
                App.SelfTestNoDialogs = savedNoDialogs;
                app.Config.LibRoot = savedRoot;
                app.Config.GenProvider = savedProvider;
                app.Config.GenApiKey = savedKey;
                app.Config.PendingCloudId = savedPending;
                ConfigStore.DirOverride = savedDirOverride;
                GenApi.EndpointOverride = savedEndpoint;
                RelatedVideo.Invalidate();
                try { mock.Stop(); } catch { }
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }

            string realIndexAfter = "";
            try { if (File.Exists(realIndex)) realIndexAfter = File.ReadAllText(realIndex, Encoding.UTF8); }
            catch { }
            Hit(sb, ref pass, ref total, "真实库 index.json 未被改动",
                string.Equals(realIndexBefore, realIndexAfter, StringComparison.Ordinal), "");
            Hit(sb, ref pass, ref total, "真实工单队列数量不变",
                CloudGen.PendingCount() == realJobsBefore, "队列数=" + realJobsBefore);
            Hit(sb, ref pass, ref total, "LibRoot / 服务商 / Key / 端点已还原",
                string.Equals(app.Config.LibRoot, savedRoot, StringComparison.OrdinalIgnoreCase)
                && string.Equals(app.Config.GenProvider, savedProvider, StringComparison.Ordinal)
                && string.Equals(app.Config.GenApiKey, savedKey, StringComparison.Ordinal)
                && string.Equals(GenApi.EndpointOverride, savedEndpoint, StringComparison.Ordinal), "");
            return Finish(sb, pass, total);
        }
    }
}
