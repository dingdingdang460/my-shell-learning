using System;
using System.Collections.Generic;
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
            bool savedNoRegenDialogs = App.SelfTestNoRegenDialogs;
            MessageBoxResult savedRegenAnswer = App.SelfTestRegenAnswer;
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
                App.SelfTestNoRegenDialogs = true;
                App.SelfTestRegenAnswer = MessageBoxResult.No;
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

                // ---- 11-14 深夜：有片时「云端生成」入口也要能用（重新生成） ----
                string mainVid = CloudGen.VideoPath(app.Config.LibRoot, probe.Id);
                File.WriteAllBytes(mainVid, Encoding.UTF8.GetBytes("existing clip"));
                RelatedVideo.Invalidate();
                Hit(sb, ref pass, ref total, "造一份规范主文件，模拟已有片",
                    app.HasCloudVideo(probe), "");

                int regenBefore = App.RegenDialogCount;
                int cfgBefore = App.CloudDialogCount;
                app.RegenCloud(probe);                 // 自检里选「否」= 取消
                Hit(sb, ref pass, ref total, "取消重新生成：确实弹了确认",
                    App.RegenDialogCount == regenBefore + 1, "");
                Hit(sb, ref pass, ref total, "取消后没写工单、旧片还在",
                    !File.Exists(CloudGen.JobPath(probe.Id)) && File.Exists(mainVid), "");
                Hit(sb, ref pass, ref total, "确认文案写明会再花钱",
                    App.LastRegenDialog.IndexOf("再花一次钱") >= 0, "");

                // 确认后也要过「没配 API 不许写假工单」的总闸（force 也不例外）
                App.SelfTestRegenAnswer = MessageBoxResult.Yes;
                string fmsg;
                bool forced = app.QueueCloudGen(probe, probe.ImagePath, true, out fmsg);
                Hit(sb, ref pass, ref total, "重新生成也必须先配 API，不写工单",
                    !forced && !File.Exists(CloudGen.JobPath(probe.Id)), fmsg);
                Hit(sb, ref pass, ref total, "重新生成的缺配置弹窗说清「重新生成」",
                    App.CloudDialogCount == cfgBefore + 1
                    && App.LastCloudDialog.IndexOf("重新生成一片需要先配好") >= 0, "");
                Hit(sb, ref pass, ref total, "重新生成没扣费、没改动旧片",
                    fmsg.IndexOf("没有扣费") >= 0 && File.Exists(mainVid), fmsg);
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
                App.SelfTestNoRegenDialogs = savedNoRegenDialogs;
                App.SelfTestRegenAnswer = savedRegenAnswer;
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

                // 11-14 深夜：改成按画面判定，必须用真实图片 + 真实视频当样本。
                // 源图 / 源片只读复制进隔离库（仓库内临时目录），不碰真实库任何文件。
                string realRoot = savedRoot;
                string realPic = Path.Combine(realRoot, "images", "biliart-17403571-4.jpg");
                string realNewPic = Path.Combine(realRoot, "images", "biliart-27606684-1.jpg");
                string realOrphan = Path.Combine(realRoot, "videos",
                    "Anime_illustration_of_a_girl_w_2026-11-12T02-18-26.mp4");
                bool samplesOk = File.Exists(realPic) && File.Exists(realNewPic) && File.Exists(realOrphan);
                Hit(sb, ref pass, ref total, "真实库里能取到内容比对样本", samplesOk,
                    samplesOk ? "图 / 新图 / 散片都在" : "样本缺失");
                if (!samplesOk) return Finish(sb, pass, total);

                WallpaperItem probe = new WallpaperItem();
                probe.Id = "orphan-guard-01";
                probe.Title = "画面确认同一张自检";
                probe.File = "images/" + probe.Id + ".jpg";
                probe.ImagePath = Path.Combine(lib, "images", probe.Id + ".jpg");
                File.Copy(realPic, probe.ImagePath, true);

                WallpaperItem fresh = new WallpaperItem();
                fresh.Id = "orphan-new-01";
                fresh.Title = "刚下载的新图（画面不同）";
                fresh.File = "images/" + fresh.Id + ".jpg";
                fresh.ImagePath = Path.Combine(lib, "images", fresh.Id + ".jpg");
                File.Copy(realNewPic, fresh.ImagePath, true);

                // 指纹不可用的占位图，只用来让 KnownKeys 非空，不参与画面匹配
                Put(lib, "images/landscape.jpg", Encoding.UTF8.GetBytes("not a real image"));

                string orphanRel = "videos/Anime_girl_waving_2026-11-12T10-02-47.mp4";
                string orphan = Path.Combine(lib, orphanRel);
                File.Copy(realOrphan, orphan, true);
                RelatedVideo.Invalidate();

                string canonical = CloudGen.VideoPath(lib, probe.Id);
                Hit(sb, ref pass, ref total, "画面确认同一张的散片被认出来",
                    app.HasCloudVideo(probe), "");
                Hit(sb, ref pass, ref total, "状态机报 Ready（菜单不会再说要生成）",
                    CloudGen.StateOf(app.Config, probe) == CloudGen.State.Ready, "");
                Hit(sb, ref pass, ref total, "画面不同的新图绝不被认领（不串片）",
                    !app.HasCloudVideo(fresh), "");
                Hit(sb, ref pass, ref total, "新图状态仍是 None（可以正常去云端生成）",
                    CloudGen.StateOf(app.Config, fresh) == CloudGen.State.None, "");

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

                // 反例：画面不同的散片、同前缀别的 id，都不能被误认
                File.Delete(orphan);
                Put(lib, "videos/totally-unrelated.mp4", Encoding.UTF8.GetBytes("not a real video"));
                Put(lib, "videos/" + probe.Id + "0.mp4", Encoding.UTF8.GetBytes("not a real video"));
                RelatedVideo.Invalidate();
                Hit(sb, ref pass, ref total, "画面不同的视频 / 同前缀别的 id 不误认",
                    !app.HasCloudVideo(probe) && !app.HasCloudVideo(fresh), "");
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

        /// <summary>
        /// 11-14 深夜：主人「不要就近认领……你要做到区分图片与视频是否为同一张图片背景」。
        /// 这个自检只读真实库：把「按名字明确归属」的 (图, 视频) 当正样本、别的图当负样本，
        /// 再把所有未归属散片对每张图的距离矩阵打进日志，用来标定 MAD / dHash 阈值。
        /// 全程只读，不写仓库外任何文件。
        /// </summary>
        public static string ContentGuard(App app)
        {
            StringBuilder sb = new StringBuilder();
            int pass = 0, total = 0;
            string root = app.Config.LibRoot;
            string vdir = Path.Combine(root, "videos");

            List<WallpaperItem> stills = new List<WallpaperItem>();
            for (int i = 0; i < app.Items.Count; i++)
            {
                WallpaperItem x = app.Items[i];
                if (!x.IsVideo && !string.IsNullOrEmpty(x.Id)) stills.Add(x);
            }

            List<string> pics = new List<string>();
            List<VideoFingerprint.Fp> pf = new List<VideoFingerprint.Fp>();
            for (int i = 0; i < stills.Count; i++)
            {
                WallpaperItem it = stills[i];
                string p = "";
                if (!string.IsNullOrEmpty(it.ImagePath) && File.Exists(it.ImagePath)) p = it.ImagePath;
                else if (!string.IsNullOrEmpty(it.File))
                {
                    string q = Path.Combine(root, it.File.Replace('/', '\\'));
                    if (File.Exists(q)) p = q;
                }
                pics.Add(p);
                pf.Add(p.Length > 0 ? VideoFingerprint.Image(p) : null);
            }
            int okFp = 0;
            for (int i = 0; i < pf.Count; i++) if (pf[i] != null) okFp++;
            Hit(sb, ref pass, ref total, "静态图指纹全部可用", okFp == stills.Count && okFp > 0,
                okFp + "/" + stills.Count);

            // ---- 正 / 负样本：按名字明确归属的规范视频 ----
            double maxPosMad = 0;
            int maxPosHash = 0, posCount = 0;
            double minNegMad = double.MaxValue;
            int minNegHash = 64, negCount = 0;
            for (int i = 0; i < stills.Count; i++)
            {
                string own = CloudGen.VideoPath(root, stills[i].Id);
                if (!File.Exists(own) || pf[i] == null) continue;
                VideoFingerprint.Fp vf = VideoFingerprint.Video(own);
                int hd;
                double mad = VideoFingerprint.Compare(pf[i], vf, out hd);
                sb.Append("  [正] ").Append(stills[i].Id).Append(" ↔ ").Append(Path.GetFileName(own))
                  .Append("  MAD=").Append(mad.ToString("0.000")).Append(" hash=").Append(hd).Append("\r\n");
                if (mad < double.MaxValue)
                {
                    posCount++;
                    if (mad > maxPosMad) maxPosMad = mad;
                    if (hd > maxPosHash) maxPosHash = hd;
                }
                for (int j = 0; j < stills.Count; j++)
                {
                    if (i == j) continue;
                    string other = CloudGen.VideoPath(root, stills[j].Id);
                    if (!File.Exists(other)) continue;
                    VideoFingerprint.Fp of = VideoFingerprint.Video(other);
                    int hd2;
                    double mad2 = VideoFingerprint.Compare(pf[i], of, out hd2);
                    sb.Append("  [负] ").Append(stills[i].Id).Append(" ↔ ").Append(Path.GetFileName(other))
                      .Append("  MAD=").Append(mad2.ToString("0.000")).Append(" hash=").Append(hd2).Append("\r\n");
                    if (mad2 < minNegMad) minNegMad = mad2;
                    if (hd2 < minNegHash) minNegHash = hd2;
                    negCount++;
                }
            }
            sb.Append("  正样本 ").Append(posCount).Append(" 对，MAD 最大 ").Append(maxPosMad.ToString("0.000"))
              .Append(" / hash 最大 ").Append(maxPosHash).Append("\r\n");
            sb.Append("  负样本 ").Append(negCount).Append(" 对，MAD 最小 ").Append(minNegMad.ToString("0.000"))
              .Append(" / hash 最小 ").Append(minNegHash).Append("\r\n");
            Hit(sb, ref pass, ref total, "有正样本和负样本", posCount > 0 && negCount > 0, "");
            Hit(sb, ref pass, ref total, "画面指纹能把同图 / 异图分开",
                maxPosMad < minNegMad, "正最大 " + maxPosMad.ToString("0.000")
                                      + " < 负最小 " + minNegMad.ToString("0.000"));

            // ---- 未归属散片 × 每张图 的距离矩阵 ----
            List<string> vids = new List<string>();
            if (Directory.Exists(vdir))
            {
                string[] fs = Directory.GetFiles(vdir, "*");
                for (int i = 0; i < fs.Length; i++)
                {
                    string n = Path.GetFileName(fs[i]).ToLowerInvariant();
                    if (n.IndexOf(".mp4", StringComparison.Ordinal) < 0
                        || n.IndexOf(".mp4.bak", StringComparison.Ordinal) >= 0) continue;
                    vids.Add(fs[i]);
                }
                string[] subs = Directory.GetDirectories(vdir);
                for (int d = 0; d < subs.Length; d++)
                {
                    string[] sfs = Directory.GetFiles(subs[d], "*");
                    for (int i = 0; i < sfs.Length; i++)
                    {
                        string n = Path.GetFileName(sfs[i]).ToLowerInvariant();
                        if (n.IndexOf(".mp4", StringComparison.Ordinal) >= 0) vids.Add(sfs[i]);
                    }
                }
            }
            sb.Append("  ---- 未归属散片 对 静态图 距离矩阵 ----\r\n");
            for (int v = 0; v < vids.Count; v++)
            {
                string file = vids[v];
                string name = Path.GetFileName(file);
                bool owned = false;
                for (int i = 0; i < stills.Count; i++)
                    if (name.ToLowerInvariant().StartsWith(stills[i].Id.ToLowerInvariant(), StringComparison.Ordinal))
                    { owned = true; break; }
                if (owned) continue;                 // 名字已明确归属，不用比画面

                VideoFingerprint.Fp vf = VideoFingerprint.Video(file);
                sb.Append("  ").Append(name).Append("\r\n");
                for (int i = 0; i < stills.Count; i++)
                {
                    if (pf[i] == null) continue;
                    int hd;
                    double mad = VideoFingerprint.Compare(pf[i], vf, out hd);
                    sb.Append("      ").Append(stills[i].Id)
                      .Append("  MAD=").Append(mad.ToString("0.000"))
                      .Append(" hash=").Append(hd)
                      .Append(VideoFingerprint.Same(mad, hd) ? "  ✔同一画面" : "")
                      .Append("\r\n");
                }
            }

            // ---- 阈值有效性：同一画面全认、不同画面全不认 ----
            int posSame = 0, negSame = 0;
            for (int i = 0; i < stills.Count; i++)
            {
                string own = CloudGen.VideoPath(root, stills[i].Id);
                if (!File.Exists(own) || pf[i] == null) continue;
                VideoFingerprint.Fp vf = VideoFingerprint.Video(own);
                int hd;
                double mad = VideoFingerprint.Compare(pf[i], vf, out hd);
                if (VideoFingerprint.Same(mad, hd)) posSame++;
                for (int j = 0; j < stills.Count; j++)
                {
                    if (i == j) continue;
                    string other = CloudGen.VideoPath(root, stills[j].Id);
                    if (!File.Exists(other)) continue;
                    VideoFingerprint.Fp of = VideoFingerprint.Video(other);
                    int hd2;
                    double mad2 = VideoFingerprint.Compare(pf[i], of, out hd2);
                    if (VideoFingerprint.Same(mad2, hd2)) negSame++;
                }
            }
            Hit(sb, ref pass, ref total, "同一画面全部判 Same、不同画面全部判不同",
                posCount > 0 && posSame == posCount && negSame == 0,
                "正 " + posSame + "/" + posCount + "，负误认 " + negSame);

            // ---- 不串片：画面不匹配的图绝不能被判成「有片」 ----
            // 11-14 深夜主人实测：刚下载的新图被就近认到别的壁纸的旧出片，点动态就播错。
            int shouldNo = 0, actuallyNo = 0;
            for (int i = 0; i < stills.Count; i++)
            {
                if (File.Exists(CloudGen.VideoPath(root, stills[i].Id))) continue;
                if (pf[i] == null) continue;
                bool anyMatch = false;
                for (int v = 0; v < vids.Count && !anyMatch; v++)
                {
                    string nm = Path.GetFileName(vids[v]).ToLowerInvariant();
                    bool owned = false;
                    for (int k = 0; k < stills.Count; k++)
                        if (nm.StartsWith(stills[k].Id.ToLowerInvariant(), StringComparison.Ordinal))
                        { owned = true; break; }
                    if (owned) continue;
                    VideoFingerprint.Fp vf = VideoFingerprint.Video(vids[v]);
                    int hd;
                    double mad = VideoFingerprint.Compare(pf[i], vf, out hd);
                    if (VideoFingerprint.Same(mad, hd)) anyMatch = true;
                }
                if (anyMatch) continue;
                shouldNo++;
                if (!app.HasCloudVideo(stills[i])) actuallyNo++;
            }
            // 11-15：真实库会变。主人用 WorkBuddy 积分给最后一张没片的图也出了片之后，
            // 库里就再也找不到「应该判无片」的负样本了，旧断言写死 shouldNo > 0 会误报。
            // 注意不是所有图都有「规范主文件」：biliart-17714251-4 是靠画面匹配
            // 认领到 Anime_illustration... 那段散片，同样合法。所以无负样本时改验两条
            // 更硬的：① 每张静态图都能找到片源可播；② 阈值没把不同画面误判成同一画面。
            // 有负样本时仍按原样逐张核对，不放宽任何一条「不串片」判定。
            int playable = 0;
            for (int i = 0; i < stills.Count; i++)
                if (app.HasCloudVideo(stills[i])) playable++;
            bool allPlayable = stills.Count > 0 && playable == stills.Count;
            Hit(sb, ref pass, ref total, "画面不匹配的图一律判「没有飘动视频」（不串片）",
                shouldNo == actuallyNo && (shouldNo > 0 || (allPlayable && negSame == 0)),
                shouldNo + " 张应为无片，实判 " + actuallyNo + " 张"
                + (shouldNo == 0
                    ? "（库里已无负样本；改验每张图都有合法片源 " + playable + "/"
                      + stills.Count + "，且不同画面零误认 " + negSame + "）"
                    : ""));

            // ---- 现场：每张图现在被判成有片 / 没片 ----
            sb.Append("  ---- 现场判定 ----\r\n");
            for (int i = 0; i < stills.Count; i++)
                sb.Append("  ").Append(stills[i].Id).Append("  HasCloudVideo=")
                  .Append(app.HasCloudVideo(stills[i])).Append("\r\n");

            return Finish(sb, pass, total);
        }

        /// <summary>
        /// 11-14 深夜：主人「怎么我切换了壁纸，上一个壁纸的动态视频还显示播放中？」
        /// 复现整条状态链：开播 A → 切到静态 B → 切到 C 开播 → 新片启动失败 →
        /// 配置残留但播放器已死。逐项断言卡片角标、Config 状态、托盘文案。
        /// 全隔离：LibRoot / config 指向仓库内 .selftest-tmp，播放器动作被注入跳过，
        /// 不设系统壁纸、不动 D:\Wallpaper、不碰 %APPDATA%、不起真播放器。
        /// </summary>
        public static string SwitchGuard(App app)
        {
            StringBuilder sb = new StringBuilder();
            int pass = 0, total = 0;

            string savedLib = app.Config.LibRoot;
            string savedDir = ConfigStore.DirOverride;
            bool savedDyn = app.Config.DynamicEnabled;
            string savedDynId = app.Config.LastDynamicId;
            string savedLastId = app.Config.LastId;
            List<WallpaperItem> savedItems = app.Items;
            int savedOverride = app.SelfTestAliveOverride;
            bool savedNoOps = app.SelfTestNoPlayerOps;

            string tmp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".selftest-tmp", "badge");
            string lib = Path.Combine(tmp, "lib");
            string cfg = Path.Combine(tmp, "cfg");

            try
            {
                Directory.CreateDirectory(Path.Combine(lib, "videos"));
                Directory.CreateDirectory(Path.Combine(lib, "images"));
                Directory.CreateDirectory(cfg);
                ConfigStore.DirOverride = cfg;
                app.Config.LibRoot = lib;
                app.SelfTestNoPlayerOps = true;
                app.SelfTestAliveOverride = 1;      // 假装播放器在跑

                WallpaperItem a = BadgeItem("zz-badge-a", "甲图");
                WallpaperItem b = BadgeItem("zz-badge-b", "乙图");
                WallpaperItem c = BadgeItem("zz-badge-c", "丙图");
                app.Items = new List<WallpaperItem>();
                app.Items.Add(a); app.Items.Add(b); app.Items.Add(c);

                MainWindow w = app.SelfTestWindow;
                Hit(sb, ref pass, ref total, "自检窗口已就绪", w != null, "");
                if (w == null) return Finish(sb, pass, total);

                w.RefreshList();
                Hit(sb, ref pass, ref total, "三张测试卡都建出角标控件",
                    w.SelfTestBadgeText(a.Id) != null
                    && w.SelfTestBadgeText(b.Id) != null
                    && w.SelfTestBadgeText(c.Id) != null, "");
                Hit(sb, ref pass, ref total, "隔离库是空的（不会误认出飘动片）",
                    !app.HasCloudVideo(a) && !app.HasCloudVideo(b) && !app.HasCloudVideo(c), "");

                // ---- 1. 开播 A ----
                app.MarkPlaying(a);
                Hit(sb, ref pass, ref total, "开播 A：A 显示「播放中」",
                    w.SelfTestBadgeText(a.Id) == "▶ 播放中", w.SelfTestBadgeText(a.Id));
                Hit(sb, ref pass, ref total, "开播 A：B / C 不显示「播放中」",
                    w.SelfTestBadgeText(b.Id) != "▶ 播放中" && w.SelfTestBadgeText(c.Id) != "▶ 播放中", "");
                Hit(sb, ref pass, ref total, "开播 A：总闸打开且 id 指向 A",
                    app.Config.DynamicEnabled && app.Config.LastDynamicId == a.Id, "");
                Hit(sb, ref pass, ref total, "开播 A：状态栏当前是甲图",
                    w.SelfTestStatusRight().IndexOf("甲图") >= 0, w.SelfTestStatusRight());
                Hit(sb, ref pass, ref total, "开播 A：托盘显示动态播放中",
                    app.SelfTestTrayText().IndexOf("播放中") >= 0, app.SelfTestTrayText());

                // ---- 2. 切到静态 B：走 DoSet 成功后的同一个收口，不真设系统壁纸 ----
                app.SelfTestAliveOverride = 0;      // 播放器已被收掉
                app.MarkStopped();
                Hit(sb, ref pass, ref total, "切静态：A 不再显示「播放中」",
                    w.SelfTestBadgeText(a.Id) != "▶ 播放中", w.SelfTestBadgeText(a.Id));
                Hit(sb, ref pass, ref total, "切静态：B 也不显示「播放中」",
                    w.SelfTestBadgeText(b.Id) != "▶ 播放中", "");
                Hit(sb, ref pass, ref total, "切静态：总闸已关", !app.Config.DynamicEnabled, "");
                Hit(sb, ref pass, ref total, "切静态：LastDynamicId 已清空（旧片不会复活）",
                    app.Config.LastDynamicId == "", app.Config.LastDynamicId);
                Hit(sb, ref pass, ref total, "切静态：托盘不再显示播放中",
                    app.SelfTestTrayText().IndexOf("播放中") < 0, app.SelfTestTrayText());
                Hit(sb, ref pass, ref total, "切静态：IsPlayingNow 为假", !app.IsPlayingNow(), "");

                // ---- 3. 切到 C 开播：角标必须跟着走，A 不能复活 ----
                app.SelfTestAliveOverride = 1;
                app.MarkPlaying(c);
                Hit(sb, ref pass, ref total, "切到 C：C 显示「播放中」",
                    w.SelfTestBadgeText(c.Id) == "▶ 播放中", w.SelfTestBadgeText(c.Id));
                Hit(sb, ref pass, ref total, "切到 C：A 不复活",
                    w.SelfTestBadgeText(a.Id) != "▶ 播放中", w.SelfTestBadgeText(a.Id));
                Hit(sb, ref pass, ref total, "切到 C：状态栏当前是丙图",
                    w.SelfTestStatusRight().IndexOf("丙图") >= 0, w.SelfTestStatusRight());

                // ---- 4. 换到一张根本播不了的视频：失败也不能留着上一张在播 ----
                WallpaperItem bad = BadgeItem("zz-badge-bad", "坏片");
                bad.IsVideo = true;
                bad.File = "videos/no-such-file.mp4";
                app.Items.Add(bad);
                w.RefreshList();
                app.MarkPlaying(c);
                Hit(sb, ref pass, ref total, "失败前 C 确实在播",
                    w.SelfTestBadgeText(c.Id) == "▶ 播放中", "");
                app.StartDynamic(bad);
                Hit(sb, ref pass, ref total, "启动失败：C 的「播放中」被清掉",
                    w.SelfTestBadgeText(c.Id) != "▶ 播放中", w.SelfTestBadgeText(c.Id));
                Hit(sb, ref pass, ref total, "启动失败：A 也不复活",
                    w.SelfTestBadgeText(a.Id) != "▶ 播放中", "");
                Hit(sb, ref pass, ref total, "启动失败：总闸关闭", !app.Config.DynamicEnabled, "");
                Hit(sb, ref pass, ref total, "启动失败：LastDynamicId 已清空",
                    app.Config.LastDynamicId == "", app.Config.LastDynamicId);
                Hit(sb, ref pass, ref total, "启动失败：托盘不含播放中",
                    app.SelfTestTrayText().IndexOf("播放中") < 0, app.SelfTestTrayText());
                Hit(sb, ref pass, ref total, "启动失败：没碰真实播放器进程",
                    app.SelfTestNoPlayerOps, "");

                // ---- 5. 配置万一残留、但进程已经死了：角标不许撒谎 ----
                app.Config.DynamicEnabled = true;
                app.Config.LastDynamicId = a.Id;
                app.SelfTestAliveOverride = 0;
                w.RefreshBadges();
                app.UpdateTrayTip();
                Hit(sb, ref pass, ref total, "进程已死：配置残留也不显示「播放中」",
                    w.SelfTestBadgeText(a.Id) != "▶ 播放中", w.SelfTestBadgeText(a.Id));
                Hit(sb, ref pass, ref total, "进程已死：托盘不认假状态",
                    app.SelfTestTrayText().IndexOf("播放中") < 0, app.SelfTestTrayText());
                Hit(sb, ref pass, ref total, "IsPlaying 对 alive=false 返回假",
                    !app.IsPlaying(a, false) && app.IsPlaying(a, true), "");

                // ---- 6. 进程回来时角标要能自己恢复（看门狗每 4 秒刷一次）----
                app.SelfTestAliveOverride = 1;
                w.RefreshBadges();
                Hit(sb, ref pass, ref total, "进程回来：角标实时恢复「播放中」",
                    w.SelfTestBadgeText(a.Id) == "▶ 播放中", w.SelfTestBadgeText(a.Id));
            }
            catch (Exception ex)
            {
                Hit(sb, ref pass, ref total, "自检过程无异常", false,
                    ex.GetType().Name + "：" + ex.Message);
            }
            finally
            {
                app.Config.DynamicEnabled = savedDyn;
                app.Config.LastDynamicId = savedDynId;
                app.Config.LastId = savedLastId;
                app.Config.LibRoot = savedLib;
                app.Items = savedItems;
                app.SelfTestAliveOverride = savedOverride;
                app.SelfTestNoPlayerOps = savedNoOps;
                ConfigStore.DirOverride = savedDir;
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }

            return Finish(sb, pass, total);
        }

        static WallpaperItem BadgeItem(string id, string title)
        {
            WallpaperItem it = new WallpaperItem();
            it.Id = id;
            it.Title = title;
            it.Tag = "other";
            it.IsVideo = false;
            return it;
        }

        /// <summary>
        /// 11-15 凌晨：Dynamic.Launch 改成「窗口没上屏就如实报失败」，
        /// 得真跑一次播放器才能确认没把正常开播搞坏。
        /// 只读库里一个 mp4 试播约 1 秒再收掉；config 隔离到仓库内临时目录，
        /// 不设系统壁纸、不改真实配置、不碰 D:\Wallpaper 里任何文件。
        /// </summary>
        public static string LaunchGuard(App app)
        {
            StringBuilder sb = new StringBuilder();
            int pass = 0, total = 0;
            string savedDir = ConfigStore.DirOverride;
            string tmp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".selftest-tmp", "launch");

            try
            {
                Directory.CreateDirectory(tmp);
                ConfigStore.DirOverride = tmp;
                app.SelfTestAliveOverride = -1;      // 这一项就是要看真实进程

                string vdir = Path.Combine(app.Config.LibRoot, "videos");
                string[] mp4 = Directory.Exists(vdir)
                    ? Directory.GetFiles(vdir, "*.mp4") : new string[0];
                Hit(sb, ref pass, ref total, "库里有可试播的 mp4", mp4.Length > 0, mp4.Length + " 个");
                Hit(sb, ref pass, ref total, "播放器 exe 已构建", Dynamic.PlayerExists, Dynamic.PlayerPath);
                Hit(sb, ref pass, ref total, "开播前没有残留播放器", !Dynamic.Alive, "");
                if (mp4.Length == 0 || !Dynamic.PlayerExists) return Finish(sb, pass, total);

                string err;
                bool ok = Dynamic.Start(mp4[0], 30, out err);
                bool alive = Dynamic.Alive;
                Hit(sb, ref pass, ref total, "窗口正常上屏时 Start 返回成功", ok, "err=" + err);
                Hit(sb, ref pass, ref total, "播放器进程确实起来了", alive, "");
                Hit(sb, ref pass, ref total, "播放器窗口挂在桌面层", Dynamic.WindowAlive, "");

                // 真进程在跑 → 状态判定必须跟着真进程走
                app.Config.DynamicEnabled = true;
                app.Config.LastDynamicId = "zz-launch-probe";
                app.UpdateTrayTip();
                Hit(sb, ref pass, ref total, "真进程在跑时 IsPlayingNow 为真", app.IsPlayingNow(), "");
                Hit(sb, ref pass, ref total, "托盘按真进程显示播放中",
                    app.SelfTestTrayText().IndexOf("播放中") >= 0, app.SelfTestTrayText());

                Dynamic.Stop();
                Hit(sb, ref pass, ref total, "Stop 后播放器进程收干净", !Dynamic.Alive, "");
                Hit(sb, ref pass, ref total, "Stop 后 IsPlayingNow 立刻变假（不留播放中）",
                    !app.IsPlayingNow(), "");
                app.MarkStopped();
                Hit(sb, ref pass, ref total, "MarkStopped 后总闸与 lastDynamicId 都清干净",
                    !app.Config.DynamicEnabled && app.Config.LastDynamicId == "", "");
            }
            catch (Exception ex)
            {
                Hit(sb, ref pass, ref total, "自检过程无异常", false,
                    ex.GetType().Name + "：" + ex.Message);
            }
            finally
            {
                try { Dynamic.Stop(); } catch { }
                app.Config.DynamicEnabled = false;
                app.Config.LastDynamicId = "";
                app.SelfTestAliveOverride = -1;
                ConfigStore.DirOverride = savedDir;
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
            return Finish(sb, pass, total);
        }

        /// <summary>
        /// 11-15：主人问「云端生成能不能用 WorkBuddy 里免费的 Hy4 / Hy3」。
        /// 自检只验口径与副作用：面板说明、代出片指令、右键菜单入口，
        /// 以及「纯函数确实零下单、零请求、零文件写入」。
        /// 不写剪贴板、不写工单、不构造设置窗口，全程只读。
        /// </summary>
        public static string BuddyHint(App app)
        {
            StringBuilder sb = new StringBuilder();
            int pass = 0, total = 0;
            string note = CloudGen.BuddyNote;
            string menu = CloudGen.BuddyMenuLabel;
            string tip = CloudGen.BuddyMenuTip;

            Hit(sb, ref pass, ref total, "面板说明提到 Hy4 / Hy3",
                note.IndexOf("Hy4") >= 0 && note.IndexOf("Hy3") >= 0, "");
            Hit(sb, ref pass, ref total, "说明写明这两个模型生成不了 mp4",
                note.IndexOf("生成不了 mp4") >= 0, "");
            Hit(sb, ref pass, ref total, "说明写明 exe 不共享 WorkBuddy 额度 / Key",
                note.IndexOf("不共享") >= 0 && note.IndexOf("API Key") >= 0, "");
            Hit(sb, ref pass, ref total, "说明写明限免期限与每日额度",
                note.IndexOf("2026-11-15 23:59") >= 0 && note.IndexOf("每日有免费额度") >= 0, "");
            Hit(sb, ref pass, ref total, "说明写明多模态出片消耗积分（不冒充免费）",
                note.IndexOf("积分") >= 0, "");
            Hit(sb, ref pass, ref total, "说明不含「免费出片 / 0 元」误导",
                note.IndexOf("免费出片") < 0 && note.IndexOf("0 元") < 0, "");
            Hit(sb, ref pass, ref total, "说明写明一次确认即全自动、不再要复制粘贴",
                note.IndexOf("一次确认") >= 0 && note.IndexOf("不用复制指令") >= 0
                && note.IndexOf("不用粘贴") >= 0, "");
            Hit(sb, ref pass, ref total, "说明写入项目级无水印硬规则",
                note.IndexOf(CloudGen.NoWatermarkRuleCn) >= 0, "");
            Hit(sb, ref pass, ref total, "模型目录里绝不混入 hy4 / hy3（选了也调不通）",
                GenModels.Find("hy4") == null && GenModels.Find("hy3") == null
                && GenModels.Find("Hy4 preview") == null, "");

            Hit(sb, ref pass, ref total, "菜单入口文案含 WorkBuddy / 不填 Key",
                menu.IndexOf("WorkBuddy") >= 0 && menu.IndexOf("不填 Key") >= 0, "");
            Hit(sb, ref pass, ref total, "菜单提示写明会消耗积分",
                tip.IndexOf("积分") >= 0, "");

            WallpaperItem still = BadgeItem("zz-buddy-hint", "说明测试图");
            WallpaperItem video = BadgeItem("zz-buddy-video", "视频条目");
            video.IsVideo = true;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string lib = Path.Combine(baseDir, ".selftest-tmp", "buddy-lib");
            string img = Path.Combine(lib, "images", "zz-buddy-hint.jpg");
            string text = CloudGen.BuildBuddyRequest(still, img, lib, 3);
            string target = Path.Combine(lib, "videos", still.Id + ".mp4");

            Hit(sb, ref pass, ref total, "指令里有壁纸 id", text.IndexOf(still.Id) >= 0, "");
            Hit(sb, ref pass, ref total, "指令里有源图绝对路径", text.IndexOf(img) >= 0, "");
            Hit(sb, ref pass, ref total, "指令里落到 videos\\<id>.mp4", text.IndexOf(target) >= 0, target);
            Hit(sb, ref pass, ref total, "指令写明 3 秒 / 720P / 无水印",
                text.IndexOf("3 秒") >= 0 && text.IndexOf("720P") >= 0 && text.IndexOf("无水印") >= 0, "");
            Hit(sb, ref pass, ref total, "指令复用统一提示词（镜头静止 / 可循环）",
                text.IndexOf("Camera completely static") >= 0 && text.IndexOf("Seamless loop") >= 0, "");
            Hit(sb, ref pass, ref total, "指令写明消耗积分、不是对话免费额度",
                text.IndexOf("积分") >= 0 && text.IndexOf("Hy4 / Hy3") >= 0, "");
            Hit(sb, ref pass, ref total, "程序里那一次确认即授权，客户端不再二次确认",
                text.IndexOf("不要再向用户二次确认") >= 0
                && text.IndexOf("点过「确认生成」授权") >= 0, "");
            Hit(sb, ref pass, ref total, "指令写入无水印硬规则与出片后抽帧验收",
                text.IndexOf(CloudGen.NoWatermarkRuleCn) >= 0
                && text.IndexOf("抽帧检查四角") >= 0, "");
            Hit(sb, ref pass, ref total, "指令写明已有同画面片子就不重出",
                text.IndexOf("同一画面的片子") >= 0, "");
            Hit(sb, ref pass, ref total, "秒数超过上限被夹到 3 秒",
                CloudGen.BuildBuddyRequest(still, img, lib, 99).IndexOf("3 秒") >= 0, "");
            Hit(sb, ref pass, ref total, "空条目返回空串",
                CloudGen.BuildBuddyRequest(null, img, lib, 3) == "", "");
            Hit(sb, ref pass, ref total, "指令里没有服务商地址 / Key（不会去直连）",
                text.IndexOf("dashscope") < 0 && text.IndexOf("Bearer") < 0
                && text.IndexOf("ark.cn-beijing") < 0, "");
            Hit(sb, ref pass, ref total, "说明与指令口径一致（都提 Hy4/Hy3 与积分）",
                note.IndexOf("积分") >= 0 && text.IndexOf("积分") >= 0
                && note.IndexOf("Hy4") >= 0 && text.IndexOf("Hy4") >= 0, "");

            MainWindow w = app == null ? null : app.SelfTestWindow;
            Hit(sb, ref pass, ref total, "自检窗口已就绪", w != null, "");
            if (w != null)
            {
                Hit(sb, ref pass, ref total, "静态图右键菜单真有代出片入口",
                    w.SelfTestHasBuddyMenuItem(still), "");
                Hit(sb, ref pass, ref total, "视频条目不出现代出片入口",
                    !w.SelfTestHasBuddyMenuItem(video), "");
            }

            // 纯函数跑完：工单队列不能多一张，也不能凭空造出目标 mp4
            int before = 0, after = 0;
            try
            {
                before = Directory.Exists(CloudGen.QueueDir)
                    ? Directory.GetFiles(CloudGen.QueueDir, "*.json").Length : 0;
            }
            catch { }
            CloudGen.BuildBuddyRequest(still, img, lib, 3);
            try
            {
                after = Directory.Exists(CloudGen.QueueDir)
                    ? Directory.GetFiles(CloudGen.QueueDir, "*.json").Length : 0;
            }
            catch { }
            Hit(sb, ref pass, ref total, "生成指令零工单写入",
                before == after && !File.Exists(CloudGen.JobPath(still.Id)),
                "queue " + before + " → " + after);
            Hit(sb, ref pass, ref total, "没有凭空造出目标 mp4 或碰到壁纸库",
                !File.Exists(target), target);

            // ---- 11-15 追加：默认通道要真能下单、显示状态、到货自动收 ----
            string savedDirOverride = ConfigStore.DirOverride;
            string savedLib = app.Config.LibRoot;
            string savedChannel = app.Config.GenChannel;
            List<WallpaperItem> savedItems = app.Items;
            bool savedNoBuddyDialogs = App.SelfTestNoBuddyDialogs;
            MessageBoxResult savedBuddyAnswer = App.SelfTestBuddyAnswer;
            bool savedNoClipboard = App.SelfTestNoClipboard;
            bool savedNoPlay = App.SelfTestNoPlay;
            bool savedNoPlayerOps = app.SelfTestNoPlayerOps;
            bool? savedClientAlive = BuddyBridge.SelfTestClientAlive;
            bool? savedHeartbeat = BuddyBridge.SelfTestHeartbeatFresh;
            bool savedNoLaunch = BuddyBridge.SelfTestNoLaunch;
            string savedCopied = App.LastCopiedText;
            int queueBefore = CloudGen.PendingCount();

            string tmp2 = Path.Combine(baseDir, ".selftest-tmp", "buddy-channel");
            string lib2 = Path.Combine(tmp2, "lib");
            string img2 = Path.Combine(lib2, "images", still.Id + ".jpg");
            try
            {
                Directory.CreateDirectory(Path.Combine(lib2, "images"));
                Directory.CreateDirectory(Path.Combine(lib2, "videos"));
                File.WriteAllBytes(img2, Encoding.UTF8.GetBytes("buddy probe image"));
                still.ImagePath = img2;
                still.File = "images/" + still.Id + ".jpg";
                ConfigStore.DirOverride = Path.Combine(tmp2, "cfg");
                app.Config.LibRoot = lib2;
                app.Config.GenChannel = "buddy";
                app.Items = new List<WallpaperItem>();
                app.Items.Add(still);
                App.SelfTestNoBuddyDialogs = true;
                App.SelfTestBuddyAnswer = MessageBoxResult.Yes;
                App.SelfTestNoClipboard = true;
                App.SelfTestNoPlay = true;
                App.LastCopiedText = "";         // 主流程不许再往剪贴板写任何东西
                app.SelfTestNoPlayerOps = true;
                BuddyBridge.SelfTestClientAlive = true;      // 不依赖本机恰好开着客户端
                BuddyBridge.SelfTestHeartbeatFresh = true;
                BuddyBridge.SelfTestNoLaunch = true;         // 禁止自检真去拉起 GUI
                CloudGen.RemoveBuddyJob(still.Id);
                RelatedVideo.Invalidate();

                Hit(sb, ref pass, ref total, "默认出片通道是 WorkBuddy 积分",
                    app.IsBuddyChannel(), "");
                app.Config.GenChannel = "api";
                Hit(sb, ref pass, ref total, "显式切成 api 才走按秒付费直连",
                    !app.IsBuddyChannel(), "");
                app.Config.GenChannel = "buddy";

                string chanLabel = "";
                int chanIndex = -1;
                try
                {
                    SettingsWindow sw = new SettingsWindow(app);
                    chanLabel = sw.SelfTestChannelLabel();
                    chanIndex = sw.SelfTestChannelSelectedIndex();
                    sw.Close();
                }
                catch (Exception ex) { chanLabel = "× 异常 " + ex.Message; }
                Hit(sb, ref pass, ref total, "设置面板默认选中 WorkBuddy 积分",
                    chanLabel.IndexOf("WorkBuddy") >= 0 && chanIndex == 0, chanLabel);

                int dlgBefore = App.BuddyDialogCount;
                app.StartBuddyGen(still);
                string job = CloudGen.BuddyJobPath(still.Id);
                string jobText = File.Exists(job) ? File.ReadAllText(job) : "";
                Hit(sb, ref pass, ref total, "点「云端生成」真的弹了积分告知",
                    App.BuddyDialogCount == dlgBefore + 1, "");
                Hit(sb, ref pass, ref total, "弹窗写明积分量级与「现在不扣积分」",
                    App.LastBuddyDialog.IndexOf("50~100") >= 0
                    && App.LastBuddyDialog.IndexOf("不扣积分") >= 0
                    && App.LastBuddyDialog.IndexOf("Hy4 / Hy3") >= 0, "");
                Hit(sb, ref pass, ref total, "真的登记了 WorkBuddy 待领工单",
                    File.Exists(job), job);
                Hit(sb, ref pass, ref total, "工单标注积分通道与待领状态",
                    jobText.IndexOf("workbuddy-credits") >= 0
                    && jobText.IndexOf("waiting-for-workbuddy") >= 0, "");
                Hit(sb, ref pass, ref total, "工单写明源图与目标 videos\\<id>.mp4",
                    jobText.IndexOf("zz-buddy-hint.jpg") >= 0
                    && jobText.IndexOf("zz-buddy-hint.mp4") >= 0, "");
                Hit(sb, ref pass, ref total, "工单内嵌完整出片指令 request（领单会话免复制粘贴）",
                    jobText.IndexOf("request") >= 0
                    && jobText.IndexOf("不要再向用户二次确认") >= 0, "");
                Hit(sb, ref pass, ref total, "工单带无水印硬标记与中文规则",
                    jobText.IndexOf("noWatermark") >= 0
                    && jobText.IndexOf(CloudGen.NoWatermarkRuleCn) >= 0, "");
                Hit(sb, ref pass, ref total, "绝不写 queue 工单（不会误走付费直连）",
                    !File.Exists(CloudGen.JobPath(still.Id))
                    && CloudGen.PendingCount() == queueBefore, "queue=" + queueBefore);
                Hit(sb, ref pass, ref total, "生成前检测=Ok 且无需申请启动",
                    App.LastReadyState == "Ok"
                    && App.LastLaunchDetail.IndexOf("无需启动") >= 0,
                    App.LastReadyState + " / " + App.LastLaunchDetail);
                Hit(sb, ref pass, ref total, "自动提交的指令文本已生成",
                    App.LastSubmitText.IndexOf(still.Id) >= 0
                    && App.LastSubmitText.IndexOf("videos\\") >= 0, "");
                Hit(sb, ref pass, ref total, "主流程不写系统剪贴板（手动兜底才写）",
                    App.LastCopiedText == "",
                    App.LastCopiedText == "" ? "" : "LastCopiedText 非空：" + App.LastCopiedText);
                Hit(sb, ref pass, ref total, "状态是「等 Buddy 出片」而不是「生成中」",
                    CloudGen.StateOf(app.Config, still) == CloudGen.State.BuddyPending,
                    CloudGen.StateOf(app.Config, still).ToString());
                MainWindow w2 = app.SelfTestWindow;
                Hit(sb, ref pass, ref total, "卡片角标真的显示「等 Buddy 出片」",
                    w2 != null && w2.SelfTestBadgeText(still.Id) == "⏳ 等 Buddy 出片",
                    w2 == null ? "窗口空" : w2.SelfTestBadgeText(still.Id));

                // 取消：确认框只剩「是 / 否」，两种「不生成」的选择都不许登记工单
                CloudGen.RemoveBuddyJob(still.Id);
                App.SelfTestBuddyAnswer = MessageBoxResult.No;
                app.StartBuddyGen(still);
                Hit(sb, ref pass, ref total, "点「否」取消不登记工单、不扣积分",
                    !File.Exists(CloudGen.BuddyJobPath(still.Id)), "");
                Hit(sb, ref pass, ref total, "取消时确认框写明生成前检测结果",
                    App.LastBuddyDialog.IndexOf("生成前检测") >= 0, "");
                App.SelfTestBuddyAnswer = MessageBoxResult.Cancel;
                app.StartBuddyGen(still);
                Hit(sb, ref pass, ref total, "异常返回值同样不登记工单",
                    !File.Exists(CloudGen.BuddyJobPath(still.Id)), "");

                // 到货：WorkBuddy 会话把 mp4 放进 videos\ → 自动收工单、状态转 Ready
                App.SelfTestBuddyAnswer = MessageBoxResult.Yes;
                app.StartBuddyGen(still);
                string vid2 = Path.Combine(lib2, "videos", still.Id + ".mp4");
                File.WriteAllBytes(vid2, Encoding.UTF8.GetBytes("fake arrived clip"));
                RelatedVideo.Invalidate();
                app.SweepCloudJobs();
                Hit(sb, ref pass, ref total, "片子到货自动收掉待领工单",
                    !File.Exists(CloudGen.BuddyJobPath(still.Id)), "");
                Hit(sb, ref pass, ref total, "到货后状态转为 Ready（有片可播）",
                    CloudGen.StateOf(app.Config, still) == CloudGen.State.Ready,
                    CloudGen.StateOf(app.Config, still).ToString());
                Hit(sb, ref pass, ref total, "收货过程没有补写 queue 工单",
                    !File.Exists(CloudGen.JobPath(still.Id)), "");
            }
            catch (Exception ex)
            {
                Hit(sb, ref pass, ref total, "通道自检过程无异常", false,
                    ex.GetType().Name + "：" + ex.Message);
            }
            finally
            {
                try { CloudGen.RemoveBuddyJob(still.Id); } catch { }
                app.Config.LibRoot = savedLib;
                app.Config.GenChannel = savedChannel;
                ConfigStore.DirOverride = savedDirOverride;
                app.Items = savedItems;
                App.SelfTestNoBuddyDialogs = savedNoBuddyDialogs;
                App.SelfTestBuddyAnswer = savedBuddyAnswer;
                App.SelfTestNoClipboard = savedNoClipboard;
                App.SelfTestNoPlay = savedNoPlay;
                app.SelfTestNoPlayerOps = savedNoPlayerOps;
                BuddyBridge.SelfTestClientAlive = savedClientAlive;
                BuddyBridge.SelfTestHeartbeatFresh = savedHeartbeat;
                BuddyBridge.SelfTestNoLaunch = savedNoLaunch;
                App.LastCopiedText = savedCopied;
                RelatedVideo.Invalidate();
                try { if (Directory.Exists(tmp2)) Directory.Delete(tmp2, true); } catch { }
            }

            return Finish(sb, pass, total);
        }

        /// <summary>
        /// 11-15：主人要求把「复制指令 → 打开任务」压缩成「点击生成 → 确认 → 自动出片自动播放」，
        /// 且生成前要检测 WorkBuddy 是否启动可用，不可用申请启动（v0.9.4 主自检）。
        ///
        /// 全隔离：临时库 / 临时配置 / 假客户端 exe / 假心跳都在仓库内 .selftest-tmp，
        /// 不拉起真客户端（SelfTestNoLaunch）、不写系统剪贴板、不写真实 jobs\buddy\ready.json、
        /// 不设系统壁纸、不写 D:\Wallpaper / %APPDATA%。
        /// 覆盖：BuddyBridge 三态检测与心跳新鲜窗口、NoClient 申请启动成功 / 失败两路、
        /// 确认一次即自动提交（含 RegenCloud 不叠第二层确认）、取消不登记、
        /// 工单带 request / 无水印字段、客户端路径配置往返。
        /// </summary>
        public static string AutoGenFlow(App app)
        {
            StringBuilder sb = new StringBuilder();
            int pass = 0, total = 0;

            string savedDir = ConfigStore.DirOverride;
            string savedLib = app.Config.LibRoot;
            string savedChannel = app.Config.GenChannel;
            string savedBuddyExe = app.Config.BuddyExe;
            List<WallpaperItem> savedItems = app.Items;
            bool savedNoBuddyDialogs = App.SelfTestNoBuddyDialogs;
            MessageBoxResult savedBuddyAnswer = App.SelfTestBuddyAnswer;
            bool savedNoClipboard = App.SelfTestNoClipboard;
            bool savedNoPlay = App.SelfTestNoPlay;
            bool savedNoPlayerOps = app.SelfTestNoPlayerOps;
            bool? savedClientAlive = BuddyBridge.SelfTestClientAlive;
            bool? savedHeartbeat = BuddyBridge.SelfTestHeartbeatFresh;
            string savedHeartbeatPath = BuddyBridge.SelfTestHeartbeatPath;
            bool savedNoLaunch = BuddyBridge.SelfTestNoLaunch;
            bool savedNoExe = BuddyBridge.SelfTestNoExe;
            int savedLaunchCount = BuddyBridge.LaunchCount;
            string savedPickerFailure = BuddyBridge.SelfTestPickerFailure;
            string savedCopied = App.LastCopiedText;

            string tmp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".selftest-tmp", "autogen");
            string lib = Path.Combine(tmp, "lib");
            string cfg = Path.Combine(tmp, "cfg");
            string hb = Path.Combine(tmp, "ready.json");
            string fakeExe = Path.Combine(tmp, "WorkBuddy.exe");

            WallpaperItem still = BadgeItem("zz-autogen", "一键生成测试图");
            try
            {
                Directory.CreateDirectory(Path.Combine(lib, "images"));
                Directory.CreateDirectory(Path.Combine(lib, "videos"));
                Directory.CreateDirectory(cfg);
                string img = Path.Combine(lib, "images", "zz-autogen.jpg");
                File.WriteAllBytes(img, Encoding.UTF8.GetBytes("autogen probe image"));
                File.WriteAllBytes(fakeExe, Encoding.UTF8.GetBytes("fake client exe"));
                still.ImagePath = img;
                still.File = "images/zz-autogen.jpg";

                ConfigStore.DirOverride = cfg;
                app.Config.LibRoot = lib;
                app.Config.GenChannel = "buddy";
                app.Config.BuddyExe = fakeExe;
                app.Items = new List<WallpaperItem>();
                app.Items.Add(still);
                App.SelfTestNoBuddyDialogs = true;
                App.SelfTestBuddyAnswer = MessageBoxResult.Yes;
                App.SelfTestNoClipboard = true;
                App.SelfTestNoPlay = true;
                app.SelfTestNoPlayerOps = true;
                App.LastCopiedText = "";
                BuddyBridge.SelfTestNoLaunch = true;   // 禁止自检真去拉起 GUI
                CloudGen.RemoveBuddyJob(still.Id);
                RelatedVideo.Invalidate();

                // ---- 一、三态检测：Ok / ClientOnly / NoClient 的判定与如实文案 ----
                BuddyBridge.SelfTestHeartbeatPath = hb;
                File.WriteAllText(hb, "{\"ts\":0}", Encoding.UTF8);
                BuddyBridge.SelfTestClientAlive = true;
                BuddyBridge.SelfTestHeartbeatFresh = true;
                string reason;
                BuddyBridge.ReadyState st = BuddyBridge.Check(out reason);
                Hit(sb, ref pass, ref total, "客户端在跑 + 心跳新鲜 → 检测为 Ok",
                    st == BuddyBridge.ReadyState.Ok && reason.IndexOf("就绪") >= 0, reason);
                BuddyBridge.SelfTestHeartbeatFresh = false;
                st = BuddyBridge.Check(out reason);
                Hit(sb, ref pass, ref total, "客户端在跑但没心跳 → 如实报 ClientOnly",
                    st == BuddyBridge.ReadyState.ClientOnly && reason.IndexOf("自动提交工单") >= 0, reason);
                BuddyBridge.SelfTestClientAlive = false;
                st = BuddyBridge.Check(out reason);
                Hit(sb, ref pass, ref total, "客户端不在 → 如实报 NoClient",
                    st == BuddyBridge.ReadyState.NoClient && reason.IndexOf("没检测到") >= 0, reason);

                // ---- 二、心跳新鲜窗口：新鲜 / 过期 / 未来 / 缺文件 / 乱码（自检路径，不碰真实心跳）----
                BuddyBridge.SelfTestHeartbeatFresh = null;
                long now = (long)(DateTime.UtcNow
                    - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                File.WriteAllText(hb, "{\"ts\":" + now + "}", Encoding.UTF8);
                Hit(sb, ref pass, ref total, "心跳新鲜窗口内算新鲜", BuddyBridge.HeartbeatFresh(), "");
                File.WriteAllText(hb, "{\"ts\":" + (now - 7200) + "}", Encoding.UTF8);
                Hit(sb, ref pass, ref total, "心跳 2 小时前算过期", !BuddyBridge.HeartbeatFresh(), "");
                File.WriteAllText(hb, "{\"ts\":" + (now + 9999) + "}", Encoding.UTF8);
                Hit(sb, ref pass, ref total, "心跳来自未来（时钟错乱）不算新鲜", !BuddyBridge.HeartbeatFresh(), "");
                File.Delete(hb);
                Hit(sb, ref pass, ref total, "没有心跳文件不算新鲜", !BuddyBridge.HeartbeatFresh(), "");
                File.WriteAllText(hb, "not json", Encoding.UTF8);
                Hit(sb, ref pass, ref total, "心跳文件乱码不算新鲜", !BuddyBridge.HeartbeatFresh(), "");

                // ---- 三、就绪时一次确认即自动提交（可用直接生成）----
                BuddyBridge.SelfTestClientAlive = true;
                BuddyBridge.SelfTestHeartbeatFresh = true;
                int dlgBefore = App.BuddyDialogCount;
                app.StartBuddyGen(still);
                string job = CloudGen.BuddyJobPath(still.Id);
                Hit(sb, ref pass, ref total, "就绪时只弹一次确认（不多弹）",
                    App.BuddyDialogCount == dlgBefore + 1, "");
                Hit(sb, ref pass, ref total, "就绪时确认后自动提交工单（无需启动）",
                    File.Exists(job) && App.LastReadyState == "Ok"
                    && App.LastLaunchDetail.IndexOf("无需启动") >= 0, job);
                Hit(sb, ref pass, ref total, "确认框写明一次确认即授权、无复制粘贴",
                    App.LastBuddyDialog.IndexOf("生成前检测") >= 0
                    && App.LastBuddyDialog.IndexOf("不用复制指令") >= 0
                    && App.LastBuddyDialog.IndexOf("不会再向你要第二次确认") >= 0, "");
                Hit(sb, ref pass, ref total, "全程不写系统剪贴板",
                    App.LastCopiedText == "", "");
                CloudGen.RemoveBuddyJob(still.Id);

                // ---- 三·二、客户端自带 CLI 在场：确认框必须说清「程序直接请求客户端出片」 ----
                // 这一支才是主人要的形态，之前自检根本没覆盖到（假客户端目录里没有 cli 资源，
                // cliOk 恒为 false，永远只验工单文案）。这里补一个假 CLI 入口把它点亮。
                string fakeCliDir = Path.Combine(tmp, @"resources\app.asar.unpacked\cli\bin");
                Directory.CreateDirectory(fakeCliDir);
                File.WriteAllText(Path.Combine(fakeCliDir, "codebuddy"), "// selftest fake cli", Encoding.UTF8);
                BuddyCli.SelfTestAuthOverride = true;   // 授权状态由自检直接指定，不真去请求模型
                BuddyCli.SelfTestNoStart = true;        // 只验分支与文案，绝不起 node
                int cliStartBefore = BuddyCli.StartCount;
                app.StartBuddyGen(still);
                Hit(sb, ref pass, ref total, "CLI 通道在时确认框写明由程序直接调 VideoGen",
                    App.LastBuddyDialog.IndexOf("直接调用 VideoGen") >= 0
                    && App.LastBuddyDialog.IndexOf("不用等整点领单") >= 0, "");
                Hit(sb, ref pass, ref total, "确认走的是直连分支且自检绝不真起 CLI（不消耗积分）",
                    BuddyCli.StartCount == cliStartBefore + 1
                    && BuddyCli.LastDetail.IndexOf("跳过真实启动") >= 0, BuddyCli.LastDetail);
                BuddyCli.SelfTestNoStart = false;
                CloudGen.RemoveBuddyJob(still.Id);
                // 收掉假 CLI，后面的用例回到「工单排队」通道，避免两条通道互相串味
                try { Directory.Delete(Path.Combine(tmp, "resources"), true); } catch { }

                // ---- 三·三、CLI 未授权：必须说清，不许只改一行状态栏 ----
                // 主人原话：「我点击之后只显示等待生成，WorkBuddy 和壁纸程序啥反应没有」。
                // 分两种：客户端带了交互界面 → 引导授权、本次不写工单；
                //           没带交互界面（这台机器就是）→ 如实说登不了、继续排队兜底。
                Directory.CreateDirectory(fakeCliDir);
                File.WriteAllText(Path.Combine(fakeCliDir, "codebuddy"), "// selftest fake cli", Encoding.UTF8);
                BuddyCli.SelfTestAuthOverride = false;   // 明确「未授权」
                BuddyCli.SelfTestNoStart = false;

                // A) 没有交互界面：不许假装还能授权
                //    照这台机器的真实样子铺：cli\dist 里只有精简版 lite-wb，没有 codebuddy.js
                string fakeDist = Path.Combine(tmp, @"resources\app.asar.unpacked\cli\dist");
                Directory.CreateDirectory(fakeDist);
                File.WriteAllText(Path.Combine(fakeDist, "codebuddy-lite-wb.mjs"),
                                  "// selftest fake lite bundle", Encoding.UTF8);
                App.LastAuthPrompt = "";
                CloudGen.RemoveBuddyJob(still.Id);
                app.StartBuddyGen(still);
                Hit(sb, ref pass, ref total, "CLI 未授权且没有交互界面时如实说明登不了",
                    App.LastAuthPrompt.IndexOf("没法登录") >= 0
                    && App.LastAuthPrompt.IndexOf("精简版") >= 0, App.LastAuthPrompt);
                Hit(sb, ref pass, ref total, "这种情况改走排队兜底（仍写工单）",
                    File.Exists(CloudGen.BuddyJobPath(still.Id)), "");
                CloudGen.RemoveBuddyJob(still.Id);

                // B) 有交互界面：引导授权，点「是」时不写工单
                File.WriteAllText(Path.Combine(fakeDist, "codebuddy.js"),
                                  "// selftest fake tui bundle", Encoding.UTF8);
                App.LastAuthPrompt = "";
                app.StartBuddyGen(still);
                Hit(sb, ref pass, ref total, "CLI 未授权但有交互界面时弹出授权引导",
                    App.LastAuthPrompt.IndexOf("/login") >= 0
                    && App.LastAuthPrompt.IndexOf("授权窗口") >= 0, "");
                Hit(sb, ref pass, ref total, "引导框给出「再点一次云端生成」的明确出口",
                    App.LastAuthPrompt.IndexOf("再点一次") >= 0, "");
                Hit(sb, ref pass, ref total, "点「是」去授权时本次不写工单（不留没人领的单）",
                    !File.Exists(CloudGen.BuddyJobPath(still.Id)), "");
                BuddyCli.SelfTestAuthOverride = null;    // 后面的用例回到「CLI 不可用」的默认态
                try { Directory.Delete(Path.Combine(tmp, "resources"), true); } catch { }

                // ---- 四、不可用时点「是」= 授权申请启动：启动成功才提交 ----
                BuddyBridge.SelfTestClientAlive = false;
                BuddyBridge.SelfTestHeartbeatFresh = false;
                int launchBefore = BuddyBridge.LaunchCount;
                app.StartBuddyGen(still);
                Hit(sb, ref pass, ref total, "NoClient 确认框写明会先申请启动客户端",
                    App.LastBuddyDialog.IndexOf("先申请启动 WorkBuddy 客户端") >= 0, "");
                Hit(sb, ref pass, ref total, "申请启动成功后仍自动提交工单",
                    File.Exists(job) && BuddyBridge.LaunchCount == launchBefore + 1, "");
                Hit(sb, ref pass, ref total, "按配置里的客户端路径启动（优先自动发现结果）",
                    BuddyBridge.LastLaunchPath == fakeExe, BuddyBridge.LastLaunchPath);
                Hit(sb, ref pass, ref total, "启动结果如实汇报", App.LastLaunchDetail.Length > 0, "");
                CloudGen.RemoveBuddyJob(still.Id);

                // ---- 五、找不到客户端 exe：点「是」也如实失败，不写工单、不留假状态 ----
                BuddyBridge.SelfTestNoExe = true;
                launchBefore = BuddyBridge.LaunchCount;
                app.StartBuddyGen(still);
                Hit(sb, ref pass, ref total, "找不到客户端时不写工单",
                    !File.Exists(job), "");
                Hit(sb, ref pass, ref total, "失败细节如实给出（引导填路径 / 手动打开）",
                    App.LastLaunchDetail.IndexOf("没找到") >= 0
                    && App.LastLaunchDetail.IndexOf("手动打开") >= 0, App.LastLaunchDetail);
                BuddyBridge.SelfTestNoExe = false;

                // ---- 六、不可用时点「否」：什么都不做，也不申请启动 ----
                App.SelfTestBuddyAnswer = MessageBoxResult.No;
                launchBefore = BuddyBridge.LaunchCount;
                app.StartBuddyGen(still);
                Hit(sb, ref pass, ref total, "点「否」取消：不申请启动、不写工单",
                    !File.Exists(job) && BuddyBridge.LaunchCount == launchBefore, "");
                App.SelfTestBuddyAnswer = MessageBoxResult.Yes;

                // ---- 七、RegenCloud 积分通道不叠第二层确认 ----
                dlgBefore = App.BuddyDialogCount;
                app.RegenCloud(still);
                Hit(sb, ref pass, ref total, "「重新生成一片」积分通道同样只弹一次确认",
                    App.BuddyDialogCount == dlgBefore + 1 && File.Exists(job), "");
                Hit(sb, ref pass, ref total, "重出确认框写明旧片保留",
                    App.LastBuddyDialog.IndexOf("旧片保留") >= 0, "");
                CloudGen.RemoveBuddyJob(still.Id);

                // ---- 八、工单字段：内嵌 request 与无水印硬标记 ----
                app.StartBuddyGen(still);
                string jobText = File.Exists(job) ? File.ReadAllText(job) : "";
                Hit(sb, ref pass, ref total, "工单内嵌完整出片指令 request",
                    jobText.IndexOf("request") >= 0
                    && jobText.IndexOf("不要再向用户二次确认") >= 0, "");
                Hit(sb, ref pass, ref total, "工单带 noWatermark / watermark:false 硬标记",
                    jobText.IndexOf("noWatermark") >= 0
                    && jobText.IndexOf("\"watermark\": false") >= 0, "");
                Hit(sb, ref pass, ref total, "工单写入中文无水印硬规则",
                    jobText.IndexOf(CloudGen.NoWatermarkRuleCn) >= 0, "");
                Hit(sb, ref pass, ref total, "工单标注积分通道与待领状态",
                    jobText.IndexOf("workbuddy-credits") >= 0
                    && jobText.IndexOf("waiting-for-workbuddy") >= 0, "");

                // ---- 九、客户端路径配置：设置面板往返 + 存取往返 ----
                string panelText = "";
                try
                {
                    SettingsWindow sw = new SettingsWindow(app);
                    sw.SelfTestSetBuddyExe(fakeExe);
                    panelText = sw.SelfTestBuddyExeText();
                    sw.Close();
                }
                catch (Exception ex) { panelText = "× 异常 " + ex.Message; }
                Hit(sb, ref pass, ref total, "设置面板客户端路径输入框往返一致",
                    panelText == fakeExe, panelText);
                // ---- 六、11-15 晚饭后：领单被 429 挡回时，程序必须说得出原因 ----
                // 主人原话：「点了生成只显示等待，WorkBuddy 和壁纸程序啥反应没有」。
                // 真相是补跑会话 21 秒就被 429 额度限流打回，程序却一个字都没透传出来。
                BuddyBridge.SelfTestNoExe = false;
                BuddyBridge.SelfTestClientAlive = true;
                BuddyBridge.SelfTestHeartbeatFresh = true;
                BuddyBridge.SelfTestPickerFailure =
                    "429 您的使用量已超出频率限制，将在 2026-11-15 21:18:21 UTC+8 重置";
                App.SelfTestBuddyAnswer = MessageBoxResult.Yes;
                CloudGen.RemoveBuddyJob(still.Id);
                app.StartBuddyGen(still);
                Hit(sb, ref pass, ref total, "确认框直接写出上次领单的真实失败原因（429 不再被藏起来）",
                    App.LastBuddyDialog.IndexOf("上次领单结局：失败") >= 0
                    && App.LastBuddyDialog.IndexOf("429") >= 0, "");
                Hit(sb, ref pass, ref total, "失败原因带重置时间，主人能判断什么时候再试",
                    App.LastBuddyDialog.IndexOf("21:18") >= 0, "");

                // ---- 七、工单排队超一轮 + 心跳不新鲜 = 没人领（不许无限挂⏳）----
                string jp = CloudGen.BuddyJobPath(still.Id);
                Hit(sb, ref pass, ref total, "刚提交的工单不判「未领单」",
                    File.Exists(jp) && !CloudGen.BuddyJobStuck(still.Id), "");
                File.SetLastWriteTimeUtc(jp, DateTime.UtcNow.AddMinutes(-120));
                BuddyBridge.SelfTestHeartbeatFresh = false;
                Hit(sb, ref pass, ref total, "排队 2 小时 + 心跳不新鲜 → 判「未领单」",
                    CloudGen.BuddyJobStuck(still.Id), "");
                MainWindow w3 = app.SelfTestWindow;
                if (w3 != null) w3.RefreshBadges();
                Hit(sb, ref pass, ref total, "卡片角标改成「⚠ 未领单」，不再挂⏳干等",
                    w3 != null && w3.SelfTestBadgeText(still.Id) == "⚠ 未领单",
                    w3 == null ? "窗口空" : w3.SelfTestBadgeText(still.Id));
                BuddyBridge.SelfTestHeartbeatFresh = true;
                if (w3 != null) w3.RefreshBadges();
                Hit(sb, ref pass, ref total, "领单心跳恢复新鲜后回到「等 Buddy 出片」",
                    w3 != null && w3.SelfTestBadgeText(still.Id) == "⏳ 等 Buddy 出片",
                    w3 == null ? "窗口空" : w3.SelfTestBadgeText(still.Id));

                // ---- 八、「为什么没出片」原因框：看完还能一键重新生成 ----
                // 主人 21:00：「触发未领单后要能申请重新生成，现在做不到」。
                BuddyBridge.SelfTestClientAlive = false;
                BuddyBridge.SelfTestHeartbeatFresh = false;
                App.SelfTestBuddyAnswer = MessageBoxResult.Yes;
                File.SetLastWriteTimeUtc(jp, DateTime.UtcNow.AddMinutes(-120));
                int launchBefore2 = BuddyBridge.LaunchCount;
                app.ShowBuddyStuck(still);
                Hit(sb, ref pass, ref total, "原因框写明工单号、排队时长与客户端状态",
                    App.LastStuckDialog.IndexOf(still.Id) >= 0
                    && App.LastStuckDialog.IndexOf("WorkBuddy 客户端：没在运行") >= 0, "");
                Hit(sb, ref pass, ref total, "原因框把上次 429 结局一起摆出来",
                    App.LastStuckDialog.IndexOf("429") >= 0, "");
                Hit(sb, ref pass, ref total, "原因框给出「重新提交」的明确出口（不再是死路）",
                    App.LastStuckDialog.IndexOf("重新提交") >= 0
                    && App.LastStuckDialog.IndexOf("点「是」") >= 0, "");
                Hit(sb, ref pass, ref total, "客户端没开时点「是」先申请把它拉起来",
                    BuddyBridge.LaunchCount == launchBefore2 + 1, "");
                Hit(sb, ref pass, ref total, "重新提交后旧工单被刷新（不再判「未领单」）",
                    File.Exists(jp) && !CloudGen.BuddyJobStuck(still.Id), "");
                MainWindow w4 = app.SelfTestWindow;
                if (w4 != null) w4.RefreshBadges();
                Hit(sb, ref pass, ref total, "角标从「⚠ 未领单」回到「等 Buddy 出片」",
                    w4 != null && w4.SelfTestBadgeText(still.Id) == "⏳ 等 Buddy 出片",
                    w4 == null ? "窗口空" : w4.SelfTestBadgeText(still.Id));

                // 客户端已开：不再重复拉起，但仍要能重新提交
                BuddyBridge.SelfTestClientAlive = true;
                BuddyBridge.SelfTestHeartbeatFresh = false;
                File.SetLastWriteTimeUtc(jp, DateTime.UtcNow.AddMinutes(-120));
                int launchBefore3 = BuddyBridge.LaunchCount;
                app.ShowBuddyStuck(still);
                Hit(sb, ref pass, ref total, "客户端已经开着就不再重复拉起",
                    BuddyBridge.LaunchCount == launchBefore3, "");
                Hit(sb, ref pass, ref total, "客户端开着时点「是」同样能重新提交（原来只有 OK）",
                    App.LastStuckDialog.IndexOf("重新提交") >= 0
                    && File.Exists(jp) && !CloudGen.BuddyJobStuck(still.Id), "");

                // 点「否」绝不动旧工单
                File.SetLastWriteTimeUtc(jp, DateTime.UtcNow.AddMinutes(-120));
                DateTime beforeNo = File.GetLastWriteTimeUtc(jp);
                App.SelfTestBuddyAnswer = MessageBoxResult.No;
                app.ShowBuddyStuck(still);
                Hit(sb, ref pass, ref total, "点「否」不动旧工单、不重新提交",
                    File.Exists(jp) && File.GetLastWriteTimeUtc(jp) == beforeNo, "");
                App.SelfTestBuddyAnswer = MessageBoxResult.Yes;
                BuddyBridge.SelfTestPickerFailure = null;

                AppConfig probe = new AppConfig();
                probe.LibRoot = lib;
                probe.BuddyExe = fakeExe;
                ConfigStore.Save(probe);
                AppConfig reloaded = ConfigStore.Load();
                Hit(sb, ref pass, ref total, "客户端路径存盘后能原样读回",
                    reloaded.BuddyExe == fakeExe, reloaded.BuddyExe);
                Hit(sb, ref pass, ref total, "老配置没有 buddyExe 时读回空串不炸",
                    new AppConfig().BuddyExe != null, "");
            }
            catch (Exception ex)
            {
                Hit(sb, ref pass, ref total, "一键生成自检过程无异常", false,
                    ex.GetType().Name + "：" + ex.Message);
            }
            finally
            {
                try { CloudGen.RemoveBuddyJob(still.Id); } catch { }
                app.Config.LibRoot = savedLib;
                app.Config.GenChannel = savedChannel;
                app.Config.BuddyExe = savedBuddyExe;
                ConfigStore.DirOverride = savedDir;
                app.Items = savedItems;
                App.SelfTestNoBuddyDialogs = savedNoBuddyDialogs;
                App.SelfTestBuddyAnswer = savedBuddyAnswer;
                App.SelfTestNoClipboard = savedNoClipboard;
                App.SelfTestNoPlay = savedNoPlay;
                app.SelfTestNoPlayerOps = savedNoPlayerOps;
                BuddyBridge.SelfTestClientAlive = savedClientAlive;
                BuddyBridge.SelfTestHeartbeatFresh = savedHeartbeat;
                BuddyBridge.SelfTestHeartbeatPath = savedHeartbeatPath;
                BuddyBridge.SelfTestNoLaunch = savedNoLaunch;
                BuddyBridge.SelfTestNoExe = savedNoExe;
                BuddyBridge.SelfTestPickerFailure = savedPickerFailure;
                BuddyBridge.LaunchCount = savedLaunchCount;
                App.LastCopiedText = savedCopied;
                BuddyCli.SelfTestNoStart = false;
                BuddyCli.SelfTestForceFail = "";
                BuddyCli.SelfTestAuthOverride = null;
                RelatedVideo.Invalidate();
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }

            return Finish(sb, pass, total);
        }

        /// <summary>
        /// 11-15：主人「库里倒数第二张图切换壁纸时不会自动播放已有动态视频」。
        /// 根因是 LibraryStore.Load 只认 videos\&lt;id&gt;.mp4，把靠画面判定认领散片的
        /// biliart-17714251-4 一载入就清成 Motion=false，点卡片只设静态壁纸，
        /// 而角标用 VideoFor 仍查得到片 —— 显示「有飘动」却永远不自动播。
        /// 隔离库里复现三种现场：变体命名片、按画面认领的散片、确实无片；
        /// 最后只读核对真实库倒数第二张。全隔离：临时库在仓库内 .selftest-tmp，
        /// 不设系统壁纸、不起播放器、不写 D:\Wallpaper / %APPDATA%。
        /// </summary>
        public static string SwitchMotion(App app)
        {
            StringBuilder sb = new StringBuilder();
            int pass = 0, total = 0;

            string savedLib = app.Config.LibRoot;
            string savedDir = ConfigStore.DirOverride;
            string tmp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".selftest-tmp", "switchmotion");
            string lib = Path.Combine(tmp, "lib");
            string cfg = Path.Combine(tmp, "cfg");

            try
            {
                Directory.CreateDirectory(Path.Combine(lib, "videos"));
                Directory.CreateDirectory(Path.Combine(lib, "images"));
                Directory.CreateDirectory(cfg);
                ConfigStore.DirOverride = cfg;
                app.Config.LibRoot = lib;
                RelatedVideo.Invalidate();

                // 真实库的图和散片只读取样，复制进隔离库当比对基准，不改原件。
                string realPic = Path.Combine(savedLib, "images", "biliart-17714251-4.jpg");
                string realVid = Path.Combine(savedLib, "videos", "Anime_illustration_of_a_girl_w_2026-11-12T10-02-47.mp4");
                string otherPic = Path.Combine(savedLib, "images", "biliart-17403571-4.jpg");
                bool mats = File.Exists(realPic) && File.Exists(realVid) && File.Exists(otherPic);
                Hit(sb, ref pass, ref total, "真实库里取得到复现材料（只读）", mats,
                    Path.GetFileName(realPic) + " / " + Path.GetFileName(realVid));
                if (!mats) return Finish(sb, pass, total);

                File.Copy(otherPic, Path.Combine(lib, "images", "sm-variant-01.jpg"), true);
                File.Copy(realPic, Path.Combine(lib, "images", "sm-content-01.jpg"), true);
                File.Copy(otherPic, Path.Combine(lib, "images", "sm-none-01.jpg"), true);
                File.Copy(realVid, Path.Combine(lib, "videos", "sm-variant-01.raw.mp4"), true);
                File.Copy(realVid, Path.Combine(lib, "videos", "orphan-clip-77.mp4"), true);

                string idx = "["
                    + "{\"id\":\"sm-variant-01\",\"title\":\"变体命名片\",\"tag\":\"other\",\"file\":\"images/sm-variant-01.jpg\",\"motion\":true,\"fx\":2},"
                    + "{\"id\":\"sm-content-01\",\"title\":\"画面认领散片\",\"tag\":\"other\",\"file\":\"images/sm-content-01.jpg\",\"motion\":true,\"fx\":2},"
                    + "{\"id\":\"sm-none-01\",\"title\":\"确实无片\",\"tag\":\"other\",\"file\":\"images/sm-none-01.jpg\",\"motion\":true,\"fx\":2}"
                    + "]";
                File.WriteAllText(Path.Combine(lib, "index.json"), idx, Encoding.UTF8);

                List<WallpaperItem> loaded = LibraryStore.Load(lib);
                Hit(sb, ref pass, ref total, "索引三条都载入了", loaded.Count == 3, loaded.Count + " 条");

                WallpaperItem a = ItemById(loaded, "sm-variant-01");
                WallpaperItem b = ItemById(loaded, "sm-content-01");
                WallpaperItem c = ItemById(loaded, "sm-none-01");
                Hit(sb, ref pass, ref total, "三条 id 都在", a != null && b != null && c != null, "");
                if (a == null || b == null || c == null) return Finish(sb, pass, total);

                Hit(sb, ref pass, ref total, "变体命名片：载入后动效标记没被误清",
                    a.Motion && a.Effect == 2, "Motion=" + a.Motion + " fx=" + a.Effect);
                Hit(sb, ref pass, ref total, "画面认领散片：载入后动效标记没被误清",
                    b.Motion && b.Effect == 2, "Motion=" + b.Motion + " fx=" + b.Effect);
                Hit(sb, ref pass, ref total, "确实无片：仍然降级为静态（语义没放宽）",
                    !c.Motion && c.Effect == 0, "Motion=" + c.Motion + " fx=" + c.Effect);

                Hit(sb, ref pass, ref total, "索引载入是纯只读，没有往库里复制片子",
                    !File.Exists(Path.Combine(lib, "videos", "sm-variant-01.mp4"))
                    && !File.Exists(Path.Combine(lib, "videos", "sm-content-01.mp4")), "");

                Hit(sb, ref pass, ref total, "切换分支判定：变体命名片会走自动开播",
                    app.MotionCapable(a), "");
                Hit(sb, ref pass, ref total, "切换分支判定：画面认领散片会走自动开播",
                    app.MotionCapable(b), "");
                Hit(sb, ref pass, ref total, "切换分支判定：确实无片不会假开播",
                    !app.MotionCapable(c), "");
                Hit(sb, ref pass, ref total, "界面角标判定也认这两张有片",
                    app.HasCloudVideo(a) && app.HasCloudVideo(b) && !app.HasCloudVideo(c), "");

                string pa = RelatedVideo.Find(lib, a, a.ImagePath);
                string pb = RelatedVideo.Find(lib, b, b.ImagePath);
                Hit(sb, ref pass, ref total, "变体命名片确实能解析出可播文件",
                    !string.IsNullOrEmpty(pa) && File.Exists(pa), pa);
                Hit(sb, ref pass, ref total, "散片按画面判定认领、指向原文件（不改名不串片）",
                    !string.IsNullOrEmpty(pb) && File.Exists(pb)
                    && string.Equals(Path.GetFileName(pb), "orphan-clip-77.mp4",
                                     StringComparison.OrdinalIgnoreCase), pb);

                // ---- 真实库现场：只读核对主人报的「倒数第二张」 ----
                app.Config.LibRoot = savedLib;
                RelatedVideo.Invalidate();
                List<WallpaperItem> real = LibraryStore.Load(savedLib);
                int at = -1;
                for (int i = 0; i < real.Count; i++)
                    if (real[i].Id == "biliart-17714251-4") { at = i; break; }
                WallpaperItem target = at >= 0 ? real[at] : null;
                // 库会不断扩容，位置写死会误报；回归点在「这张的动效标记与开播判定」，不在下标
                Hit(sb, ref pass, ref total, "真实库里能找到 biliart-17714251-4",
                    at >= 0, "下标 " + at + " / 共 " + real.Count);
                Hit(sb, ref pass, ref total, "这张载入后保留动效标记",
                    target != null && target.Motion && target.Effect == 2,
                    target == null ? "未找到" : "Motion=" + target.Motion + " fx=" + target.Effect);
                Hit(sb, ref pass, ref total, "这张切换壁纸时会走自动开播",
                    target != null && app.MotionCapable(target), "");
                Hit(sb, ref pass, ref total, "片源是内容判定散片，真实库没被写新文件",
                    target != null
                    && !File.Exists(Path.Combine(savedLib, "videos", "biliart-17714251-4.mp4")), "");
            }
            catch (Exception ex)
            {
                Hit(sb, ref pass, ref total, "切换自动开播自检过程无异常", false,
                    ex.GetType().Name + "：" + ex.Message);
            }
            finally
            {
                app.Config.LibRoot = savedLib;
                ConfigStore.DirOverride = savedDir;
                RelatedVideo.Invalidate();
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
            return Finish(sb, pass, total);
        }

        static WallpaperItem ItemById(List<WallpaperItem> list, string id)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i].Id, id, StringComparison.OrdinalIgnoreCase)) return list[i];
            return null;
        }
    }
}
