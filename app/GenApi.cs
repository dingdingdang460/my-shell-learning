using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Text;

namespace WallpaperManager
{
    /// <summary>
    /// 「云端动效」的**直连 API** 实现：应用自己把图提交给服务商、轮询、下载，全程不需要外部工具。
    ///
    /// 为什么要有这个：原来只有工单制（写 JSON 等外部程序来取），装到别人机器上等于废的。
    /// 现在主人在设置里填自己的 API Key，点一下就能出片，跟 WorkBuddy 无关；
    /// 不填 Key 就还走工单制，把别处产出的 3 秒短片按 id 命名丢进 videos\&lt;id&gt;.mp4 也能收货。
    ///
    /// 支持两家（设置里切换）：
    ///   通义万相 DashScope —— wan2.6-i2v-flash，720P 无声约 0.15 元/秒，最便宜
    ///   火山方舟 Ark       —— Doubao-Seedance，效果最接近「真·飘动」，约 0.2~0.6 元/秒
    ///
    /// 两个接口的骨架一模一样（提交拿 taskId → 轮询 → 拿 video_url → 下载），
    /// 差别只在 URL、鉴权头、请求体和返回字段，所以用同一个 Submit/Poll 配上不同的适配。
    /// </summary>
    public static class GenApi
    {
        public enum Provider { None = 0, Wanx = 1, Ark = 2 }

        public static Provider ParseProvider(string s)
        {
            if (string.IsNullOrEmpty(s)) return Provider.None;
            s = s.Trim().ToLowerInvariant();
            if (s == "wanx" || s == "dashscope" || s == "通义") return Provider.Wanx;
            if (s == "ark" || s == "volc" || s == "火山") return Provider.Ark;
            return Provider.None;
        }

        public static string ProviderName(Provider p)
        {
            if (p == Provider.Wanx) return "通义万相";
            if (p == Provider.Ark) return "火山方舟";
            return "不用 API";
        }

        public static string DefaultModel(Provider p)
        {
            if (p == Provider.None) return "";
            return GenModels.DefaultFor(p).Id;
        }

        /// <summary>
        /// 自检用的接口基址覆盖。空 = 走两家的官方地址（生产路径，永远如此）。
        /// 只有 --cloudflowtest 会把它指向本机模拟服务端，用来在没有真 Key 的情况下
        /// 把「提交 → 轮询 → 下载」这条真 HTTP 链路跑通，而不是只画一个界面。
        /// </summary>
        public static string EndpointOverride = "";

        /// <summary>轮询间隔（毫秒）。自检把它调小，好让端到端验证不必干等 15 秒。</summary>
        public static int PollIntervalMs = 5000;

        static string Base(Provider p)
        {
            if (!string.IsNullOrEmpty(EndpointOverride)) return EndpointOverride.TrimEnd('/');
            return p == Provider.Wanx
                ? "https://dashscope.aliyuncs.com"
                : "https://ark.cn-beijing.volces.com";
        }

        /// <summary>这家是不是把秒数直接当参数传（通义传 duration，火山传 duration + 文本参数）</summary>
        public static bool Ready(AppConfig cfg)
        {
            if (cfg == null) return false;
            Provider p = ParseProvider(cfg.GenProvider);
            return p != Provider.None && !string.IsNullOrEmpty(cfg.GenApiKey);
        }

        // ---------------------------------------------------------------- 提交

        public sealed class SubmitResult
        {
            public bool Ok;
            public string TaskId = "";
            public string Msg = "";
        }

        public static SubmitResult Submit(AppConfig cfg, string imagePath, string prompt, int seconds)
        {
            SubmitResult r = new SubmitResult();
            Provider p = ParseProvider(cfg == null ? "" : cfg.GenProvider);
            if (p == Provider.None) { r.Msg = "还没选服务商（设置 → 云端生成）"; return r; }
            if (cfg == null || string.IsNullOrEmpty(cfg.GenApiKey)) { r.Msg = "还没填 API Key"; return r; }

            string url, auth, extra, body;
            BodyResult br = BuildBody(cfg, imagePath, prompt, seconds);
            if (!br.Ok) { r.Msg = br.Msg; return r; }
            url = br.Url; auth = br.Auth; extra = br.Extra; body = br.Body;

            string resp, err;
            if (!Http.PostJson(url, body, auth, extra, 120000, out resp, out err))
            {
                r.Msg = err;
                Log.Note("!! 云端提交失败（" + ProviderName(p) + "）：" + err);
                return r;
            }

            Log.Note("云端提交（" + ProviderName(p) + "）：" + Clip(resp, 400));

            string id = p == Provider.Wanx ? Json.PathStr(resp, "output", "task_id")
                                           : Json.PathStr(resp, "id");
            if (string.IsNullOrEmpty(id))
            {
                r.Msg = "服务端没返回 task_id：" + Clip(resp, 200);
                return r;
            }
            r.Ok = true;
            r.TaskId = id;
            r.Msg = "任务已提交（" + id + "）";
            return r;
        }

        public sealed class BodyResult
        {
            public bool Ok;
            public string Msg = "";
            public string Url = "";
            public string Auth = "";
            public string Extra = "";
            public string Body = "";
            public int ImageBytes;      // base64 之前的 JPEG 字节数
        }

        /// <summary>
        /// 只构造请求、不发送。提交和「自检」共用这一份，保证测的就是真正会发出去的那份。
        /// </summary>
        public static BodyResult BuildBody(AppConfig cfg, string imagePath, string prompt, int seconds)
        {
            BodyResult r = new BodyResult();
            Provider p = ParseProvider(cfg == null ? "" : cfg.GenProvider);
            if (p == Provider.None) { r.Msg = "还没选服务商"; return r; }

            string dataUri;
            try { dataUri = ImageDataUri(imagePath, 1280, 90L, out r.ImageBytes); }
            catch (Exception ex) { r.Msg = "读图失败：" + ex.Message; return r; }

            // 目录外的手填 ID 一律回落到该服务商默认，避免请求发出去才发现模型名不存在
            string model = GenModels.Resolve(p, cfg.GenModel).Id;
            string res = string.IsNullOrEmpty(cfg.GenResolution) ? "720P" : cfg.GenResolution;

            string url, auth, extra;
            StringBuilder sb = new StringBuilder();

            if (p == Provider.Wanx)
            {
                url = Base(p) + "/api/v1/services/aigc/video-generation/video-synthesis";
                auth = "Bearer " + cfg.GenApiKey;
                extra = "X-DashScope-Async: enable";     // 不带这个头就是同步接口，会直接超时
                sb.Append("{\"model\":").Append(Json.Quote(model));
                sb.Append(",\"input\":{\"prompt\":").Append(Json.Quote(prompt));
                sb.Append(",\"img_url\":").Append(Json.Quote(dataUri)).Append("}");
                sb.Append(",\"parameters\":{\"resolution\":").Append(Json.Quote(res));
                sb.Append(",\"duration\":").Append(seconds);
                sb.Append(",\"prompt_extend\":false,\"watermark\":false}}");
            }
            else
            {
                url = Base(p) + "/api/v3/contents/generations/tasks";
                auth = "Bearer " + cfg.GenApiKey;
                extra = null;
                sb.Append("{\"model\":").Append(Json.Quote(model));
                sb.Append(",\"content\":[{\"type\":\"text\",\"text\":").Append(Json.Quote(prompt)).Append("}");
                sb.Append(",{\"type\":\"image_url\",\"image_url\":{\"url\":").Append(Json.Quote(dataUri)).Append("}}]");
                sb.Append(",\"duration\":").Append(seconds);
                sb.Append(",\"ratio\":\"adaptive\",\"watermark\":false}");
            }

            r.Ok = true;
            r.Url = url;
            r.Auth = auth;
            r.Extra = extra == null ? "" : extra;
            r.Body = sb.ToString();
            return r;
        }

        // ---------------------------------------------------------------- 查询

        public enum PollState { Running = 0, Done = 1, Failed = -1 }

        public static PollState Poll(AppConfig cfg, string taskId, out string videoUrl, out string msg)
        {
            videoUrl = "";
            msg = "";
            Provider p = ParseProvider(cfg == null ? "" : cfg.GenProvider);

            string url = p == Provider.Wanx
                ? Base(p) + "/api/v1/tasks/" + taskId
                : Base(p) + "/api/v3/contents/generations/tasks/" + taskId;

            string resp, err;
            if (!Http.Get(url, "Bearer " + (cfg == null ? "" : cfg.GenApiKey), 30000, out resp, out err))
            {
                // 网络抖动不算任务失败，下一轮再问
                msg = err;
                return PollState.Running;
            }

            string st = p == Provider.Wanx ? Json.PathStr(resp, "output", "task_status")
                                           : Json.PathStr(resp, "status");
            st = (st ?? "").ToUpperInvariant();

            if (st == "SUCCEEDED" || st == "SUCCESS")
            {
                videoUrl = p == Provider.Wanx ? Json.PathStr(resp, "output", "video_url")
                                              : Json.PathStr(resp, "content", "video_url");
                if (string.IsNullOrEmpty(videoUrl))
                {
                    msg = "成功但没给视频地址：" + Clip(resp, 200);
                    return PollState.Failed;
                }
                return PollState.Done;
            }
            if (st == "FAILED" || st == "CANCELED" || st == "CANCELLED")
            {
                string m = Json.PathStr(resp, "output", "message");
                if (string.IsNullOrEmpty(m)) m = Json.PathStr(resp, "error", "message");
                if (string.IsNullOrEmpty(m)) m = Clip(resp, 200);
                msg = "服务端报失败：" + m;
                return PollState.Failed;
            }

            msg = st.Length > 0 ? st : "等待中";
            return PollState.Running;
        }

        // ---------------------------------------------------------------- 下载

        public static bool Download(string url, string dst, out string msg)
        {
            msg = "";
            string part = dst + ".part";
            try
            {
                if (File.Exists(part)) File.Delete(part);
                using (WebClient wc = new WebClient())
                {
                    // 不设的话 WebClient 会捡系统代理，本机模拟服务端 / 直连下载都可能被绕走
                    Net.Apply(wc);
                    wc.Headers.Add("User-Agent", "WallpaperManager/1.0");
                    wc.DownloadFile(url, part);
                }
                FileInfo fi = new FileInfo(part);
                if (fi.Length < 1024) { msg = "下载到的文件太小（" + fi.Length + " 字节），可能不是视频"; File.Delete(part); return false; }
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(part, dst);
                msg = "已下载 " + (fi.Length / 1024) + "KB";
                return true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                msg = "下载失败：" + ex.Message;
                return false;
            }
        }

        // ---------------------------------------------------------------- 图片 → data URI

        /// <summary>
        /// 把图片压成 base64 的 data URI 直接放进请求体 —— 不用图床、不用公网地址，
        /// 换台电脑也能用。1280 宽足够了（出片 720P，再大只是白花流量和额度）。
        /// </summary>
        static string ImageDataUri(string path, int maxWidth, long quality)
        {
            int ignored;
            return ImageDataUri(path, maxWidth, quality, out ignored);
        }

        static string ImageDataUri(string path, int maxWidth, long quality, out int jpegBytes)
        {
            jpegBytes = 0;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (Image img = Image.FromStream(fs, true, false))
            {
                int w = img.Width, h = img.Height;
                double s = w > maxWidth ? (double)maxWidth / w : 1.0;
                int nw = Math.Max(2, (int)Math.Round(w * s));
                int nh = Math.Max(2, (int)Math.Round(h * s));

                using (Bitmap bmp = new Bitmap(nw, nh, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(img, new Rectangle(0, 0, nw, nh));
                    }

                    ImageCodecInfo jpg = JpegCodec();
                    EncoderParameters ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);

                    using (MemoryStream ms = new MemoryStream())
                    {
                        if (jpg != null) bmp.Save(ms, jpg, ep);
                        else bmp.Save(ms, ImageFormat.Jpeg);
                        jpegBytes = (int)ms.Length;
                        return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
        }

        static ImageCodecInfo JpegCodec()
        {
            ImageCodecInfo[] all = ImageCodecInfo.GetImageEncoders();
            for (int i = 0; i < all.Length; i++)
                if (all[i].FormatID == ImageFormat.Jpeg.Guid) return all[i];
            return null;
        }

        static string Clip(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
