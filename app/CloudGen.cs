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

        /// <summary>
        /// 11-15：WorkBuddy 积分通道的「待领工单」。跟 queue\ 必须分开：
        /// queue\ = 应用直连服务商、后台线程自己会提交的单；
        /// buddy\ = 等主人在 WorkBuddy 会话里粘贴指令并确认积分后才可能出片的单。
        /// 分开放，老的「没配 API 绝不写假工单」断言依然成立，也不会被 RunApiJob 误提交。
        /// </summary>
        public static string BuddyDir { get { return Path.Combine(Dir, "buddy"); } }

        public static string BuddyJobPath(string id)
        {
            return Path.Combine(BuddyDir, id + ".json");
        }

        public static void EnsureDirs()
        {
            try
            {
                Directory.CreateDirectory(QueueDir);
                Directory.CreateDirectory(DoneDir);
                Directory.CreateDirectory(FailDir);
                Directory.CreateDirectory(BuddyDir);
            }
            catch { }
        }

        /// <summary>登记一张待领工单。只写盘，不复制剪贴板、不发请求、不扣任何积分。</summary>
        public static bool WriteBuddyJob(AppConfig cfg, WallpaperItem it,
                                         string imagePath, int seconds, out string msg)
        {
            msg = "";
            if (it == null || string.IsNullOrEmpty(it.Id)) { msg = "条目为空"; return false; }
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            { msg = "找不到这张图的本地文件，先把图下载下来"; return false; }
            if (seconds < 1) seconds = 1;
            if (seconds > MaxSeconds) seconds = MaxSeconds;
            EnsureDirs();
            try
            {
                StringBuilder s = new StringBuilder();
                s.Append("{\r\n");
                s.Append("  \"id\": ").Append(Json.Quote(it.Id)).Append(",\r\n");
                s.Append("  \"title\": ").Append(Json.Quote(it.Title)).Append(",\r\n");
                s.Append("  \"image\": ").Append(Json.Quote(imagePath)).Append(",\r\n");
                s.Append("  \"out\": ").Append(Json.Quote(VideoPath(cfg.LibRoot, it.Id))).Append(",\r\n");
                s.Append("  \"seconds\": ").Append(seconds).Append(",\r\n");
                s.Append("  \"created\": ").Append(Now()).Append(",\r\n");
                s.Append("  \"channel\": ").Append(Json.Quote("workbuddy-credits")).Append(",\r\n");
                s.Append("  \"status\": ").Append(Json.Quote("waiting-for-workbuddy")).Append(",\r\n");
                // 11-15 出片规范：无水印不是一句提示词，而是随工单一起交付的硬约束
                s.Append("  \"noWatermark\": true,\r\n");
                s.Append("  \"watermark\": false,\r\n");
                s.Append("  \"watermarkRule\": ").Append(Json.Quote(NoWatermarkRuleCn)).Append(",\r\n");
                // 完整出片指令随工单落盘，领单会话读工单即可全自动执行，不用主人复制粘贴
                s.Append("  \"request\": ")
                 .Append(Json.Quote(BuildBuddyRequest(it, imagePath, cfg.LibRoot, seconds)))
                 .Append(",\r\n");
                s.Append("  \"prompt\": ").Append(Json.Quote(BuildPrompt(it, seconds))).Append("\r\n");
                s.Append("}\r\n");
                File.WriteAllText(BuddyJobPath(it.Id), s.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { msg = "登记待领工单失败：" + ex.Message; return false; }

            Log.Note("WorkBuddy 积分通道：已自动提交待领工单 " + it.Id
                     + "（客户端在跑会自动领单出片，未扣积分）");
            msg = "已自动提交给 WorkBuddy（客户端在跑会自动出片并自动播放，未扣积分）";
            return true;
        }

        /// <summary>片子到货 / 主人取消时把待领工单收掉，卡片不再挂「等 Buddy 出片」。</summary>
        public static void RemoveBuddyJob(string id)
        {
            try
            {
                string p = BuddyJobPath(id);
                if (File.Exists(p)) File.Delete(p);
            }
            catch { }
        }

        public static int BuddyPendingCount()
        {
            try { return Directory.Exists(BuddyDir) ? Directory.GetFiles(BuddyDir, "*.json").Length : 0; }
            catch { return 0; }
        }

        /// <summary>工单躺了多久（分钟）。11-15：用来判断「已提交但没人领」，别无限挂⏳。</summary>
        public static double BuddyJobAgeMinutes(string id)
        {
            try
            {
                string p = BuddyJobPath(id);
                if (!File.Exists(p)) return 0;
                double m = (DateTime.UtcNow - File.GetLastWriteTimeUtc(p)).TotalMinutes;
                return m < 0 ? 0 : m;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 工单超过一轮领单间隔还没人动、且心跳不新鲜 → 判定「无人领单」。
        /// 这是「绝不显示假生成中」的延伸：等也得分清真在等还是根本没人在干。
        /// </summary>
        public static bool BuddyJobStuck(string id)
        {
            if (!File.Exists(BuddyJobPath(id))) return false;
            return BuddyJobAgeMinutes(id) > BuddyBridge.JobStuckMinutes
                   && !BuddyBridge.HeartbeatFresh();
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
        /// 11-15：主人发现库里倒数第二、第三张的动态视频都带水印，而旧提示词里
        /// 明明已经写了 "No watermark" —— 说明**只靠一句提示词去不掉水印**。
        /// 实测（biliart-38588664-1）进一步确认：视频生成平台按《人工智能生成合成内容
        /// 标识办法》在左上角硬打「AI生成」角标，提示词无法约束。
        /// 主人拍板的口径：**先按无水印要求生成，出片后检测角标，局部修补干净再交付**。
        /// 三条落地位置：
        ///   1) 生成侧：所有出片提示词统一拼接 NoWatermarkRule；付费直连另有
        ///      GenApi 的 "watermark":false 请求参数；积分通道工单带明文标记。
        ///   2) 验收侧：自动领单出片后必须抽帧检查；平台「AI生成」合规角标用 delogo
        ///      对角标区域做局部修补（仅左上角、区域约 88×44 内）后交付；
        ///      除合规角标外的其他水印、或修补不干净的，一律不许写进 videos\\。
        ///   3) 历史片：已有水印片标注「待重出」，由主人在「重新生成一片」里显式决定，
        ///      绝不擅自重复消耗积分。
        /// 这段常量是**唯一口径**，界面说明、工单、自检都从这儿取，不再各写一句。
        /// </summary>
        public const string NoWatermarkRule =
              "No watermark anywhere in the frame: no platform logo, no AI-generated mark, "
            + "no username, no timestamp, no text overlay, no caption, no border. "
            + "If the source image has a corner watermark, remove it before animating. "
            + "The delivered MP4 must be visually clean; a watermarked clip must not be delivered.";

        public const string NoWatermarkRuleCn =
              "生成视频必须无水印：画面里不得出现平台标识、用户名、时间、字幕或边框；"
            + "源图角落若有水印，生成时先去掉。服务商硬打的「AI生成」合规角标在提示词里"
            + "去不掉 —— 按主人拍板：先生成，出片后抽帧检查，再用 delogo 对左上角角标区域"
            + "（约 88×44 像素）做局部修补，修补干净才交付。除合规角标外的其他水印、"
            + "或修补后仍能看出痕迹的 mp4，一律不入库、不播放。";

        /// <summary>
        /// 统一的生成提示词。时长、镜头、循环、无水印四条硬约束都写进去了，
        /// 但**不能指望服务端一定照做** —— 出片后还是要过一道检查，见上方出片规范。
        /// </summary>
        public static string BuildPrompt(WallpaperItem it, int seconds)
        {
            string what = it != null && !string.IsNullOrEmpty(it.Title) ? "（" + it.Title + "）" : "";
            return "Keep this image exactly as it is" + what + ": same character identity, face, "
                 + "pose, framing, background and lighting. Add only very gentle natural motion: "
                 + "strands of hair and the hem / sleeves of clothing ripple softly in a light breeze, "
                 + "subtle breathing. Camera completely static: no pan, no zoom, no drift, no shake. "
                 + "Duration exactly " + seconds + " seconds. Seamless loop: the last frame must match "
                 + "the first frame. " + NoWatermarkRule;
        }

        // ---------------------------------------------------------------- 交给 WorkBuddy 代出片

        /// <summary>
        /// 11-15：主人问「云端生成能不能用 WorkBuddy 里配的 Hy4 / Hy3，都是免费的」。
        /// 不能，且不是「少加一个下拉项」的问题：
        ///   1) Hy4 / Hy3 是 WorkBuddy 客户端的对话 / 推理模型（图片输入 + 思考模式），
        ///      只能看图、写字，产出不了 mp4；官方公告写明「生成图片与视频会切换到
        ///      相应多模态模型完成，按正常规则产生积分消耗」，免费只覆盖对话额度，
        ///      且限免到 2026-11-15 23:59、每日有额度上限。
        ///   2) WorkBuddy 的模型配置（含内置与自定义）只供 WorkBuddy 自己转发对话用，
        ///      Key 存在本地 workbuddy/models.json，独立 exe 拿不到它的额度或凭证。
        ///   3) 这里是「提交图 → 轮询异步任务 → 下载 mp4」的图生视频接口，
        ///      跟 /chat/completions 不是同一种协议，把 hy4 塞进 model 字段只会报错。
        /// 所以模型下拉里绝不列这两个名字，免得主人以为选中就能免费出片。
        ///
        /// 但 WorkBuddy 会话本身有多模态出片能力，可以让 Buddy 代出一片、按约定命名
        /// 丢进 videos\ —— 这条路径不填 Key、不花服务商的钱，但会消耗 WorkBuddy 积分。
        /// 下面两段文案抽成常量，界面和自检共用，保证说法一致、不夸大「免费」。
        /// </summary>
        public const string BuddyNote =
              "出片通道默认「优先 WorkBuddy 积分」：点「云端生成」→ 一次确认 → "
            + "程序自动登记待领工单，WorkBuddy 客户端在跑就自动领单、用多模态模型图生视频，"
            + "mp4 落到 videos\\<id>.mp4 后程序自动识别、自动换上桌面并播放。\n"
            + "全程不用复制指令、不用粘贴、不用手动开任务，你只需那一次确认。"
            + "不花服务商的钱，但会消耗 WorkBuddy 积分 —— "
            + "不是 Hy4 / Hy3 的对话免费额度（限免到 2026-11-15 23:59，每日有免费额度，触及后消耗积分）。\n"
            + "关于 Hy4 / Hy3：它们是 WorkBuddy 客户端的对话 / 推理模型（图片输入 + 思考模式），"
            + "只能看图、写字，生成不了 mp4；本程序是独立 exe，不共享 WorkBuddy 的模型额度或 API Key，"
            + "所以模型列表里只有两家的图生视频模型，不会列出它们 —— 选了也出不了片。\n"
            + "出片规范：" + NoWatermarkRuleCn + "\n"
            + "想完全不打开 WorkBuddy 也能出片：把「出片通道」切到「服务商 API Key」，"
            + "填自己的百炼 / 方舟 Key，程序自己直连提交、轮询、下载（按秒付费，"
            + "720P 约 ¥0.45 / 3 秒）。积分通道依赖 WorkBuddy 客户端在运行"
            + "（自动领单任务在客户端里跑，每小时领一次单）；客户端没开时工单会留着等，"
            + "不会自动出片，也不会扣积分。";

        public const string BuddyMenuLabel = "⧉ 手动兜底：复制交给 WorkBuddy 的指令（不填 Key）";
        public const string BuddyMenuTip =
              "正常点「云端生成」一次确认就会自动出片，一般用不到这项。"
            + "Hy4 / Hy3 是 WorkBuddy 的对话模型，不能生成视频；这条指令让 Buddy 用多模态能力"
            + "代出片（会消耗积分）。只复制文字，不下单、不扣费。";

        /// <summary>
        /// 生成工单里随附的完整出片指令，是纯函数：
        /// 不写剪贴板、不写工单、不发请求、不碰磁盘，也绝不会自动触发积分消耗。
        /// 11-15：主流程不再要求主人复制粘贴；这段文本随待领工单落盘，
        /// 由自动领单的 WorkBuddy 会话读取后直接出片（程序里那一次确认即授权）。
        /// </summary>
        public static string BuildBuddyRequest(WallpaperItem it, string imagePath, string libRoot, int seconds)
        {
            if (it == null || string.IsNullOrEmpty(it.Id)) return "";
            if (seconds < 1) seconds = 1;
            if (seconds > MaxSeconds) seconds = MaxSeconds;
            string outPath = Path.Combine(string.IsNullOrEmpty(libRoot) ? "" : libRoot,
                                          "videos", it.Id + ".mp4");

            StringBuilder sb = new StringBuilder();
            sb.Append("请用多模态视频生成能力，把这张壁纸做成 ").Append(seconds)
              .Append(" 秒的无缝循环飘动短片。\r\n\r\n");
            sb.Append("壁纸 id：").Append(it.Id).Append("\r\n");
            sb.Append("源图（作为首帧，严格保持画面不变）：").Append(imagePath ?? "").Append("\r\n");
            sb.Append("出片后请把 MP4 直接放到：").Append(outPath).Append("\r\n");
            sb.Append("（这个路径在壁纸库里，用户已在壁纸管家点过「确认生成」授权本次出片，")
              .Append("不用再问一次；放好后壁纸管家会自动识别、自动换上桌面并播放，")
              .Append("不用在程序里填 API Key。）\r\n\r\n");
            sb.Append("要求：").Append(seconds)
              .Append(" 秒、720P、无水印无字幕、镜头完全静止、首尾帧一致可循环。\r\n");
            sb.Append("【无水印硬规则】").Append(NoWatermarkRuleCn).Append("\r\n");
            sb.Append("出片后请抽帧检查四角与边缘；服务商硬打的「AI生成」合规角标按主人拍板：")
              .Append("用 ffmpeg delogo 对左上角角标区域（约 x=2,y=4,w=88,h=44）局部修补干净；")
              .Append("除合规角标外的其他水印、或修补后仍有可见痕迹的 mp4 不许放进 videos\\。\r\n");
            sb.Append("提示词：").Append(BuildPrompt(it, seconds)).Append("\r\n\r\n");
            sb.Append("费用说明：这一步走的是视频生成模型，会消耗 WorkBuddy 积分；\r\n");
            sb.Append("Hy4 / Hy3 的限时免费只覆盖对话额度，不包含图片与视频生成。\r\n");
            sb.Append("授权说明：用户已经在壁纸管家点过「确认生成」授权本次出片，\r\n");
            sb.Append("请按平台要求说明本次积分消耗估算后直接生成，不要再向用户二次确认，\r\n");
            sb.Append("也不要把工单留在「等确认」状态 —— 全自动完成出片与交付。\r\n");
            sb.Append("另外：如果 videos\\ 里已经有跟这张图同一画面的片子，就别重新生成，")
              .Append("直接告诉我就行 —— 壁纸管家会按画面匹配并直接播放。");
            return sb.ToString();
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
            return Enqueue(cfg, it, imagePath, false, out msg);
        }

        /// <summary>
        /// force=true 只来自主人明确点的「重新生成一片」：跳过「已有视频」闸，
        /// 但先把旧片留一份 .bak-regen-* 备份，别把之前花过钱的片子弄丢。
        /// 其余路径（GenCloud / StartCloud / SetMotionFx / 后台）一律走 false。
        /// </summary>
        public static bool Enqueue(AppConfig cfg, WallpaperItem it, string imagePath, bool force, out string msg)
        {
            if (it == null || string.IsNullOrEmpty(it.Id)) { msg = "条目为空"; return false; }

            string vid = VideoPath(cfg.LibRoot, it.Id);
            if (force)
            {
                if (File.Exists(vid))
                {
                    try
                    {
                        string bak = vid + ".bak-regen-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        if (!File.Exists(bak)) File.Copy(vid, bak, false);
                        Log.Note("云端生成：重新出片前已备份旧片 ← " + Path.GetFileName(bak));
                    }
                    catch (Exception ex)
                    {
                        Log.Note("!! 重新生成：旧片备份失败（不阻断下单）：" + ex.Message);
                    }
                }
            }
            else
            {
                string ready = RelatedVideo.Find(cfg.LibRoot, it, imagePath);
                if (ready != null)
                {
                    msg = "这张已经有相关视频了（" + System.IO.Path.GetFileName(ready)
                        + "），直接播，不用重新生成";
                    return false;
                }
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

        /// <summary>
        /// Manual = 队列里躺着一张工单，但没配 API，根本不会有进程去提交它。
        /// 11-14 主人实测：「点完就显示生成中，还是假的」。以前 StateOf 一律报 Queued，
        /// 界面就跟着写「生成中」，实际那张 JSON 要挂两天才超时。现在单独分出来，
        /// 界面必须写「待人工处理」，绝不冒充正在生成。
        /// </summary>
        public enum State { None, Queued, Ready, Failed, Manual, BuddyPending }

        public static State StateOf(AppConfig cfg, WallpaperItem it)
        {
            if (it == null || string.IsNullOrEmpty(it.Id)) return State.None;
            // 11-14 深夜：队列里有新单时优先显示「等待中」。
            // 「重新生成一片」会在已有旧片的情况下写一单，这时不能再报 Ready，
            // 否则主人点了重新生成却看不到任何变化，又变成「假的」。
            if (File.Exists(JobPath(it.Id)))
                return GenApi.Ready(cfg) ? State.Queued : State.Manual;
            if (File.Exists(VideoPath(cfg.LibRoot, it.Id))) return State.Ready;
            // 11-14：备份 / 变体命名 / 关联子文件夹 / 画面确认同一张的散片，都算「已就绪」，
            // 界面菜单才不会把「播放这段视频」错写成「云端生成（要花钱）」。
            if (!it.IsVideo && RelatedVideo.Find(cfg.LibRoot, it, null) != null) return State.Ready;
            // 11-15：登记了 WorkBuddy 待领工单，但会话还没确认出片。
            // 这是真实状态，绝不写成「生成中」——不会有人背地里在跑任务。
            if (!it.IsVideo && File.Exists(BuddyJobPath(it.Id))) return State.BuddyPending;
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
