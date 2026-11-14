using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WallpaperManager
{
    /// <summary>
    /// 「云端动效」的下单与收货（11-13 起只走云端图生视频模型）。
    ///
    ///   确认（应用内）：右键 / 预览按钮 → 浮条显示模型、大概花费、预计等待；
    ///                   没配 API Key 就提示去设置，不写工单。
    ///   下单（应用内）：点「开始生成」→ 往 %APPDATA%\wallpaper\jobs\queue 写一张工单，
    ///                   同时起后台线程直连服务商（见 GenApi.cs / RunApiJob）。
    ///   生产（云端）：提交 → 轮询 → 下载 mp4 到 videos\&lt;id&gt;.mp4。
    ///                 本地脚本出片这条线已停用：它做不出内容级动态。
    ///   收货（应用内）：管理器每几秒扫一次队列，发现 mp4 到货就入库、标成云端动效，
    ///                   如果这张正在播，立刻换成真·飘动视频，不用主人再点一次。
    ///
    /// 工单是纯文本 JSON，主人可以自己看、自己改、自己删，不需要理解应用内部。
    /// 它现在只作「任务状态 + 收货凭据」用，不再被用来拉起任何外部程序。
    /// </summary>
    public static class CloudGen
    {
        /// <summary>出片时长上限（秒）。主人的要求：3 秒以内，省额度。</summary>
        public const int MaxSeconds = 3;

        /// <summary>
        /// API 直连的超时（分钟）。服务商排队 + 推理，30 分钟还不回来基本就是废单。
        /// </summary>
        public const int ApiTimeoutMinutes = 30;

        /// <summary>
        /// 工单制的超时（分钟）。这条线要等外部来取单 —— 主人自己、或者 WorkBuddy 这边，
        /// 都不是 7x24 在线的。定 30 分钟会让「下班前点一下」的单在回来时变成「失败」，
        /// 所以放到 2 天：跨得过一个下班 + 隔夜 + 周末，又不至于永远挂着假「生成中」。
        /// </summary>
        public const int JobTimeoutMinutes = 2880;

        public static string Dir { get { return Path.Combine(ConfigStore.Dir, "jobs"); } }
        public static string QueueDir { get { return Path.Combine(Dir, "queue"); } }
        public static string DoneDir { get { return Path.Combine(Dir, "done"); } }
        public static string FailDir { get { return Path.Combine(Dir, "fail"); } }

        public static void EnsureDirs()
        {
            try
            {
                Directory.CreateDirectory(QueueDir);
                Directory.CreateDirectory(DoneDir);
                Directory.CreateDirectory(FailDir);
            }
            catch { }
        }

        /// <summary>约定：图片 images\xxx.jpg ↔ 视频 videos\xxx.mp4（同名 id）</summary>
        public static string VideoPath(string libRoot, string id)
        {
            return Path.Combine(libRoot, "videos", id + ".mp4");
        }

        public static string JobPath(string id)
        {
            return Path.Combine(QueueDir, id + ".json");
        }

        /// <summary>
        /// 浮条上只放得下一行，整条命令（还带参数）塞进去会把布局撑坏。
        /// 这里只取「像程序的那一段」的文件名：gen-motion.bat。
        /// </summary>
        public static string GenCmdName(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return "";
            string[] parts = cmd.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim('"');
                string ext = Path.GetExtension(p);
                if (ext == null) continue;
                ext = ext.ToLowerInvariant();
                if (ext == ".exe" || ext == ".bat" || ext == ".cmd" || ext == ".ps1" || ext == ".py")
                    return Path.GetFileName(p);
            }
            string s = cmd.Trim().Trim('"');
            return s.Length <= 26 ? s : s.Substring(0, 26) + "…";
        }

        /// <summary>
        /// 找随程序一起发出去的出片脚本：tools\gen-motion.bat。
        /// 11-13 起本地脚本路线停用、设置面板不再有入口，这个方法只作历史兼容保留，
        /// 主流程不再调用；genCmd 字段同理只保证老配置读得回来，不会被拉起。
        /// </summary>
        public static string FindBundledScript()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
                {
                    string p = Path.Combine(dir, "tools", "gen-motion.bat");
                    if (File.Exists(p)) return p;

                    DirectoryInfo up = Directory.GetParent(dir.TrimEnd(Path.DirectorySeparatorChar));
                    if (up == null) break;
                    dir = up.FullName;
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 统一的生成提示词。三条硬约束都写进去了（时长 3 秒、镜头不动、无水印），
        /// 但**不能指望服务端一定照做** —— 出片后还是要过一道检查，见 docs 里的出片规范。
        /// </summary>
        public static string BuildPrompt(WallpaperItem it, int seconds)
        {
            string what = it != null && !string.IsNullOrEmpty(it.Title) ? "（" + it.Title + "）" : "";
            return "Keep this image exactly as it is" + what + ": same character identity, face, "
                 + "pose, framing, background and lighting. Add only very gentle natural motion: "
                 + "strands of hair and the hem / sleeves of clothing ripple softly in a light breeze, "
                 + "subtle breathing. Camera completely static: no pan, no zoom, no drift, no shake. "
                 + "Duration exactly " + seconds + " seconds. Seamless loop: the last frame must match "
                 + "the first frame. No watermark, no logo, no text overlay, no caption, no border.";
        }

        /// <summary>
        /// 下单一单。返回 false 时 msg 说明原因（图片找不到 / 已经生成过了 / 写盘失败）。
        /// 已经排队或已有视频就不重复下单，避免主人连点几次花几份额度。
        ///
        /// 11-14：这里的「已有视频」扩成「有任何相关的视频」，由 RelatedVideo 统一判定，
        /// 不再只看规范路径。写工单是花钱前的最后一步，这道闸必须留在这儿。
        /// </summary>
        public static bool Enqueue(AppConfig cfg, WallpaperItem it, string imagePath, out string msg)
        {
            if (it == null || string.IsNullOrEmpty(it.Id)) { msg = "条目为空"; return false; }

            string vid = VideoPath(cfg.LibRoot, it.Id);
            string ready = RelatedVideo.Find(cfg.LibRoot, it, imagePath);
            if (ready != null)
            {
                msg = "这张已经有相关视频了（" + System.IO.Path.GetFileName(ready)
                    + "），直接播，不用重新生成";
                return false;
            }

            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            {
                msg = "找不到这张图的本地文件，没法生成（先把图下载下来）";
                return false;
            }

            EnsureDirs();
            string job = JobPath(it.Id);
            if (File.Exists(job))
            {
                // 只说「已经在队列里」等于没说：得告诉人排了多久，才知道是不是卡住了
                string jid, jimg;
                int jsec;
                long jcreated;
                long wait = 0;
                if (ReadJob(job, out jid, out jimg, out jsec, out jcreated) && jcreated > 0)
                    wait = Now() - jcreated;
                msg = "这张已经在队列里了，排了 " + WaitText(wait) + "，还没出片";
                return false;
            }

            // 上次失败留下的标记要清掉：不然重新下单后 StateOf 还是先报 Failed，
            // 菜单文案会一直停在「上次失败」，看着像没生效。
            try
            {
                string old = Path.Combine(FailDir, it.Id + ".json");
                if (File.Exists(old)) File.Delete(old);
            }
            catch { }

            // 出片秒数：主人在 config 里可以调小，但绝不允许超过 3 秒（省额度、循环更快）
            int sec = MaxSeconds;
            if (cfg != null && cfg.GenSeconds >= 1 && cfg.GenSeconds < sec) sec = cfg.GenSeconds;

            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{\r\n");
                sb.Append("  \"id\": ").Append(Json.Quote(it.Id)).Append(",\r\n");
                sb.Append("  \"title\": ").Append(Json.Quote(it.Title)).Append(",\r\n");
                sb.Append("  \"image\": ").Append(Json.Quote(imagePath)).Append(",\r\n");
                sb.Append("  \"out\": ").Append(Json.Quote(vid)).Append(",\r\n");
                sb.Append("  \"seconds\": ").Append(sec).Append(",\r\n");
                sb.Append("  \"created\": ").Append(Now()).Append(",\r\n");
                sb.Append("  \"prompt\": ").Append(Json.Quote(BuildPrompt(it, sec))).Append("\r\n");
                sb.Append("}\r\n");
                File.WriteAllText(job, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                msg = "写工单失败：" + ex.Message;
                return false;
            }

            Log.Note("云端生成：已下单 " + it.Id + "（" + sec + " 秒）→ " + job);

            // 11-13：主人明确「脚本这条路放弃，以后只用模型云端生成」。
            // 以前这里会拉起 genCmd 指向的本地脚本出片，那条线做不出内容级动态，已移除。
            // 工单本身保留 —— 它是任务状态与「视频到货自动收货」的凭据，由 RunApiJob
            // （应用内直连云端模型）负责把 mp4 落到 out 字段，SweepCloudJobs 照常收货。
            msg = "已排入云端生成（" + sec + " 秒，无水印）";
            return true;
        }

        public enum State { None, Queued, Ready, Failed }

        public static State StateOf(AppConfig cfg, WallpaperItem it)
        {
            if (it == null || string.IsNullOrEmpty(it.Id)) return State.None;
            if (File.Exists(VideoPath(cfg.LibRoot, it.Id))) return State.Ready;
            // 11-14：备份 / 变体命名 / 关联子文件夹里的片子也算「已就绪」，
            // 界面菜单才不会把「播放这段视频」错写成「云端生成（要花钱）」。
            if (!it.IsVideo && RelatedVideo.Find(cfg.LibRoot, it, null) != null) return State.Ready;
            if (File.Exists(JobPath(it.Id))) return State.Queued;
            if (File.Exists(Path.Combine(FailDir, it.Id + ".json"))) return State.Failed;
            return State.None;
        }

        public static int PendingCount()
        {
            try
            {
                if (!Directory.Exists(QueueDir)) return 0;
                return Directory.GetFiles(QueueDir, "*.json").Length;
            }
            catch { return 0; }
        }

        /// <summary>工单里读几个字段（坏文件直接忽略，不让一条脏数据卡住整个队列）</summary>
        public static bool ReadJob(string path, out string id, out string image, out int seconds, out long created)
        {
            id = ""; image = ""; seconds = MaxSeconds; created = 0;
            try
            {
                Dictionary<string, object> d = Json.AsDict(Json.Parse(File.ReadAllText(path, Encoding.UTF8)));
                if (d == null) return false;
                id = Json.Str(d, "id");
                image = Json.Str(d, "image");
                seconds = (int)Json.Long(d, "seconds");
                created = Json.Long(d, "created");
                if (seconds <= 0) seconds = MaxSeconds;
                return id.Length > 0;
            }
            catch { return false; }
        }

        static long Now()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        public static long NowSeconds() { return Now(); }

        /// <summary>把秒数说成人话：12 秒 / 3 分 12 秒 / 2 小时 5 分</summary>
        public static string WaitText(long sec)
        {
            if (sec < 0) sec = 0;
            if (sec < 60) return sec + " 秒";
            if (sec < 3600) return (sec / 60) + " 分 " + (sec % 60) + " 秒";
            return (sec / 3600) + " 小时 " + ((sec % 3600) / 60) + " 分";
        }

        static void Move(string from, string dir, string id)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string to = Path.Combine(dir, id + ".json");
                if (File.Exists(to)) File.Delete(to);
                File.Move(from, to);
            }
            catch { }
        }

        public static void MarkDone(string jobPath, string id) { Move(jobPath, DoneDir, id); }
        public static void MarkFailed(string jobPath, string id) { Move(jobPath, FailDir, id); }
    }
}
