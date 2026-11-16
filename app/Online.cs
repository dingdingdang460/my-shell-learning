using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace WallpaperManager
{
    /// <summary>一条在线结果（还没入库）</summary>
    public class OnlineItem
    {
        public string Slug = "";
        public string MediaId = "";
        public string Title = "";
        public string Tag = "";
        public string ThumbUrl = "";  // 364x205 预览图，实测 15-19KB，用来做结果卡不烧流量
        public string PreviewUrl = ""; // 1280x720 预览图，实测 114KB，点开大图时才拉
        public string Cover4k = "";   // 4K 静态封面 jpg（约 2MB，入库时才下）
        public string Mp4Hd = "";     // 1920x1080 视频
        public string Mp4Uhd = "";    // 3840x2160 视频
        public string Page = "";      // 详情页
    }

    /// <summary>
    /// 联网搜索与入库。图源 motionbgs.com：无需 Key、无防盗链，
    /// 列表页内嵌 /media/&lt;id&gt;/&lt;slug&gt;.&lt;分辨率&gt; 标识，据此还原各资源直链。
    /// 限流：请求间隔 >= 800ms，失败重试 1 次，仍失败抛出由界面提示。
    /// </summary>
    public static class Online
    {
        public const string SiteBase = "https://motionbgs.com";
        const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";
        const int MinIntervalMs = 800;

        static DateTime lastCall = DateTime.MinValue;
        static readonly object Gate = new object();

        static void Throttle()
        {
            lock (Gate)
            {
                int gap = (int)(DateTime.UtcNow - lastCall).TotalMilliseconds;
                if (gap >= 0 && gap < MinIntervalMs) Thread.Sleep(MinIntervalMs - gap);
                lastCall = DateTime.UtcNow;
            }
        }

        static string Fetch(string url)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Throttle();
                try
                {
                    using (WebClient wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", UA);
                        wc.Encoding = Encoding.UTF8;
                        Net.Apply(wc);
                        string s = wc.DownloadString(url);
                        if (string.IsNullOrEmpty(s)) throw new Exception("服务器返回空内容");
                        return s;
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt == 0) Thread.Sleep(1200);
                }
            }
            throw new Exception("网络请求失败：" + (last == null ? "未知原因" : last.Message));
        }

        /// <summary>
        /// 纯函数：从列表页 HTML 提取条目，按 slug 去重。
        /// 列表页每个结果是 &lt;img src=/i/c/364x205/media/&lt;id&gt;/&lt;file&gt;.jpg alt="真实标题"&gt;，
        /// 所以预览图与标题都能直接拿到，不需要猜。
        /// </summary>
        public static List<OnlineItem> Parse(string html, int limit)
        {
            List<OnlineItem> list = new List<OnlineItem>();
            if (string.IsNullOrEmpty(html)) return list;

            HashSet<string> seen = new HashSet<string>();
            MatchCollection tags = Regex.Matches(html, "<img[^>]+>");
            foreach (Match t in tags)
            {
                string tag = t.Value;
                Match sm = Regex.Match(tag, "src=[\"']?(/i/c/\\d+x\\d+/media/(\\d+)/([^\"'\\s>]+)\\.jpg)");
                if (!sm.Success) continue;

                string thumbPath = sm.Groups[1].Value;
                string mediaId = sm.Groups[2].Value;
                string fileBase = sm.Groups[3].Value;
                string slug = Regex.Replace(fileBase, "\\.\\d+x\\d+$", "");
                if (slug.Length == 0) continue;
                if (seen.Contains(slug)) continue;
                seen.Add(slug);

                Match am = Regex.Match(tag, "alt=\"([^\"]*)\"");
                string title = am.Success ? CleanTitle(am.Groups[1].Value) : "";
                if (title.Length == 0) title = Titleize(slug);

                OnlineItem it = new OnlineItem();
                it.MediaId = mediaId;
                it.Slug = slug;
                it.Title = title;
                it.ThumbUrl = SiteBase + thumbPath;
                // 站内按尺寸裁图，路径前缀 /i/c/<宽>x<高>/；1280x720 实测 114KB，够看清内容又不烧流量
                it.PreviewUrl = SiteBase + "/i/c/1280x720" + thumbPath.Substring(thumbPath.IndexOf("/media/", StringComparison.Ordinal));
                it.Cover4k = SiteBase + "/media/" + mediaId + "/" + fileBase + ".jpg";
                it.Mp4Hd = SiteBase + "/media/" + mediaId + "/" + slug + ".1920x1080.mp4";
                it.Mp4Uhd = SiteBase + "/media/" + mediaId + "/" + slug + ".3840x2160.mp4";
                it.Page = SiteBase + "/" + slug;
                list.Add(it);
                if (limit > 0 && list.Count >= limit) break;
            }
            return list;
        }

        /// <summary>纯函数：去掉站点 alt 里固定的 " live wallpaper" 后缀，并还原 HTML 实体</summary>
        public static string CleanTitle(string alt)
        {
            if (string.IsNullOrEmpty(alt)) return "";
            string t = alt.Trim();
            try { t = WebUtility.HtmlDecode(t); }
            catch { }
            const string suffix = " live wallpaper";
            if (t.Length > suffix.Length && t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                t = t.Substring(0, t.Length - suffix.Length).Trim();
            return t;
        }

        /// <summary>纯函数：slug -> 可读标题</summary>
        public static string Titleize(string slug)
        {
            if (string.IsNullOrEmpty(slug)) return "";
            string[] parts = slug.Replace("-", " ").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                string w = parts[i];
                if (w.Length == 0) continue;
                sb.Append(char.ToUpperInvariant(w[0]));
                if (w.Length > 1) sb.Append(w.Substring(1));
            }
            return sb.ToString();
        }

        public static List<OnlineItem> Search(string keyword, int limit)
        {
            string url = SiteBase + "/search?q=" + Uri.EscapeDataString(keyword);
            return Parse(Fetch(url), limit);
        }

        public static List<OnlineItem> ByTag(string tag, int limit)
        {
            string url = SiteBase + "/tag:" + Uri.EscapeDataString(tag) + "/";
            return Parse(Fetch(url), limit);
        }

        /// <summary>
        /// 站点对无匹配的关键词不返回空列表，而是回一批推荐内容（实测「zzzqqqxxx」仍给 4 条无关结果），
        /// 所以不能靠「列表为空」判断没搜到，必须比对标题是否含关键词的任一词。
        /// </summary>
        public static bool LooksRelevant(string keyword, List<OnlineItem> rows)
        {
            if (string.IsNullOrEmpty(keyword) || rows == null || rows.Count == 0) return false;
            string[] words = keyword.ToLowerInvariant().Split(new char[] { ' ', '-', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return false;
            for (int i = 0; i < rows.Count; i++)
            {
                string t = (rows[i].Title == null ? "" : rows[i].Title.ToLowerInvariant());
                for (int j = 0; j < words.Length; j++)
                    if (t.IndexOf(words[j], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        // ---------------- 入库 ----------------

        /// <summary>
        /// 下载并登记为本地壁纸：视频进 videos/，4K 封面进 images/，缩略图进 thumbs/，最后追加 index.json。
        /// 已存在同 id 的视频则跳过下载（幂等）。progress 回调在后台线程上调用。
        /// </summary>
        public static WallpaperItem Install(OnlineItem o, string root, string tag, Action<string> progress,
                                            bool prefer4k = false)
        {
            if (o == null) throw new ArgumentNullException("o");
            if (string.IsNullOrEmpty(root)) throw new Exception("壁纸库路径为空");

            string dirVideos = Path.Combine(root, "videos");
            string dirImages = Path.Combine(root, "images");
            string dirThumbs = Path.Combine(root, "thumbs");
            Directory.CreateDirectory(dirVideos);
            Directory.CreateDirectory(dirImages);
            Directory.CreateDirectory(dirThumbs);

            string videoAbs = Path.Combine(dirVideos, o.Slug + ".mp4");
            string coverAbs = Path.Combine(dirImages, o.Slug + ".jpg");
            string thumbAbs = Path.Combine(dirThumbs, o.Slug + ".jpg");

            bool downloaded4k = false;
            if (!File.Exists(videoAbs) || new FileInfo(videoAbs).Length < 100000)
            {
                string url = PickVideoUrl(o, prefer4k);
                bool is4k = prefer4k && !string.IsNullOrEmpty(o.Mp4Uhd) && url == o.Mp4Uhd;
                if (progress != null) progress("正在下载视频" + (is4k ? "（4K 3840x2160）" : "（1080P）") + "：" + o.Title);
                Fetch(url, videoAbs, 100000);
                downloaded4k = is4k;
            }

            if (!File.Exists(coverAbs) || new FileInfo(coverAbs).Length < 20000)
            {
                if (progress != null) progress("正在下载封面：" + o.Title);
                try { DownloadTo(o.Cover4k, coverAbs, 20000); }
                catch { /* 封面失败不阻塞入库，用视频帧兜底 */ }
            }

            if (!File.Exists(thumbAbs))
            {
                try
                {
                    if (File.Exists(coverAbs)) MakeThumb(coverAbs, thumbAbs, 560);
                }
                catch { }
            }

            WallpaperItem it = new WallpaperItem();
            it.Id = o.Slug;
            it.Title = o.Title;
            it.Tag = string.IsNullOrEmpty(tag) ? "online" : tag;
            it.File = "videos/" + o.Slug + ".mp4";
            it.Thumb = File.Exists(thumbAbs) ? "thumbs/" + o.Slug + ".jpg" : (File.Exists(coverAbs) ? "images/" + o.Slug + ".jpg" : "");
            it.Bytes = new FileInfo(videoAbs).Length;
            it.Duration = Mp4DurationSeconds(videoAbs);
            it.Upgrade4k = o.Mp4Uhd;
            it.IsVideo = true;
            it.ThumbAbs = File.Exists(thumbAbs) ? thumbAbs : (File.Exists(coverAbs) ? coverAbs : "");
            it.ImagePath = File.Exists(coverAbs) ? coverAbs : null;
            it.Resolution = downloaded4k ? "3840x2160" : "1920x1080";

            AppendIndex(root, it, it.Resolution);
            return it;
        }

        /// <summary>
        /// 入库时该下哪个版本：开了「优先 4K」且源真有 3840x2160 才下 4K，
        /// 否则老实下 1080P（4K 的体积和解码压力都是 4 倍，1080P 屏上看不出差别）。
        /// </summary>
        public static string PickVideoUrl(OnlineItem o, bool prefer4k)
        {
            if (o == null) return "";
            if (prefer4k && !string.IsNullOrEmpty(o.Mp4Uhd)) return o.Mp4Uhd;
            return o.Mp4Hd;
        }

        /// <summary>
        /// 自检注入：替掉真实下载（url, 目标路径）→ 是否成功。
        /// 设了就绝不出网，只用本地文件模拟「下载完成」。
        /// </summary>
        public static Func<string, string, bool> SelfTestFetch;

        /// <summary>
        /// 自检注入：替掉「抽真实分辨率」的探测（路径）→ 返回 {宽, 高}，失败返回 null。
        /// </summary>
        public static Func<string, int[]> SelfTestProbe;

        /// <summary>下载：自检模式走注入，正常运行走真实网络</summary>
        static void Fetch(string url, string target, long minBytes)
        {
            if (SelfTestFetch != null)
            {
                if (!SelfTestFetch(url, target)) throw new Exception("（自检注入）下载失败：" + url);
                return;
            }
            DownloadTo(url, target, minBytes);
        }

        /// <summary>
        /// 探测视频真实分辨率。用 MF 打开读 Width/Height —— 不信文件名里的尺寸，
        /// 也不靠「文件变大了就是 4K」猜：站点可能给 404 页面或还是 1080P 的片子。
        /// 打不开返回 null。
        /// </summary>
        static int[] ProbeSize(string path)
        {
            if (SelfTestProbe != null) return SelfTestProbe(path);
            try
            {
                using (MfVideo.Reader r = MfVideo.Reader.Open(path))
                {
                    return new int[] { r.Width, r.Height };
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 把一个 1080P 条目升级成 4K（3840x2160）。成功返回空串，失败返回给主人看的原因。
        ///
        /// 规矩：
        ///   · 只有站点视频才有 4K 源（upgrade_4k），B 站图集/本地自出片一律拒绝并说明原因；
        ///   · 先下到 .part4k，用 MF 抽真实分辨率，**确认宽度 ≥ 3000 才替换**，
        ///     否则删掉临时文件、原片一个字节都不动（宁可升级失败，也不能把好片换成 1080P）；
        ///   · 旧片改名 .bak-4k 保留（4K 解码更吃显卡，主人想退回去还有原件），已有就加序号，绝不覆盖。
        /// </summary>
        public static string Upgrade(WallpaperItem it, string root, Action<string> progress)
        {
            if (it == null) return "条目为空";
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return "壁纸库目录不存在";
            if (!it.IsVideo) return "只有视频条目能升 4K（静态图的「下载 4K 封面」是另一个入口）";
            if (string.IsNullOrEmpty(it.Upgrade4k)) return "这条没有 4K 源（只有站点视频才有 3840x2160 版本）";

            string rel = it.File.Length > 0 ? it.File : "videos/" + it.Id + ".mp4";
            string target = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            string part = target + ".part4k";

            try
            {
                if (progress != null) progress("正在下载 4K 版本：" + it.Title);
                Fetch(it.Upgrade4k, part, 100000);

                long newLen = new FileInfo(part).Length;
                if (newLen < 100000)
                {
                    Drop(part);
                    return "下载内容过小（" + newLen + " 字节），可能不是有效视频，已放弃替换";
                }

                int[] sz = ProbeSize(part);
                if (sz == null || sz[0] <= 0 || sz[1] <= 0)
                {
                    Drop(part);
                    return "下载到的文件读不出分辨率（可能不是有效 mp4），已放弃替换";
                }
                if (sz[0] < 3000)
                {
                    Drop(part);
                    return "下载到的不是 4K（实测 " + sz[0] + "x" + sz[1] + "），已放弃替换，原片未动";
                }

                // 换片：旧片进 .bak-4k（不覆盖已有备份），新片就位
                string bak = target + ".bak-4k";
                if (File.Exists(bak))
                {
                    int n = 2;
                    while (File.Exists(bak + n)) n++;
                    bak = bak + n;
                }
                bool hadOld = File.Exists(target);
                if (hadOld) File.Move(target, bak);
                try
                {
                    File.Move(part, target);
                }
                catch
                {
                    // 新片没就位就把旧片放回去，别留下「删了旧的、没换成新的」
                    try { if (hadOld && !File.Exists(target)) File.Move(bak, target); } catch { }
                    throw;
                }

                it.Bytes = new FileInfo(target).Length;
                it.Duration = Mp4DurationSeconds(target);
                it.Resolution = sz[0] + "x" + sz[1];
                UpdateIndexEntry(root, it, it.Resolution);
                Log.Note("4K 升级成功：" + it.Id + " → " + it.Resolution + "，" + it.Bytes + " 字节"
                         + (hadOld ? "（旧片备份 " + Path.GetFileName(bak) + "）" : ""));
                return "";
            }
            catch (Exception ex)
            {
                Drop(part);
                return "4K 升级失败：" + ex.Message;
            }
        }

        static void Drop(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>把某条的分辨率 / 大小 / 时长写回 index.json（升级 4K 之后调）</summary>
        public static bool UpdateIndexEntry(string root, WallpaperItem it, string resolution)
        {
            string path = Path.Combine(root, "index.json");
            if (!File.Exists(path)) return false;
            try
            {
                object[] arr = Json.AsArr(Json.Parse(File.ReadAllText(path, Encoding.UTF8)));
                if (arr == null) return false;

                bool hit = false;
                for (int i = 0; i < arr.Length; i++)
                {
                    Dictionary<string, object> d = Json.AsDict(arr[i]);
                    if (d == null || Json.Str(d, "id") != it.Id) continue;
                    d["bytes"] = it.Bytes;
                    d["duration_s"] = Math.Round(it.Duration, 1);
                    d["resolution"] = resolution;
                    hit = true;
                }
                if (!hit) return false;

                File.Copy(path, path + ".bak", true);
                File.WriteAllText(path, Json.Serialize(arr), new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }

        static void DownloadTo(string url, string target, long minBytes)
        {
            string tmp = target + ".part";
            try
            {
                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", UA);
                    Net.Apply(wc);
                    wc.DownloadFile(url, tmp);
                }
                long len = new FileInfo(tmp).Length;
                if (len < minBytes) throw new Exception("下载内容过小（" + len + " 字节），可能不是有效文件");
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        /// <summary>纯函数：读 mp4 的 mvhd box 取时长（秒），失败返回 0</summary>
        public static double Mp4DurationSeconds(string path)
        {
            try
            {
                byte[] buf = File.ReadAllBytes(path);
                byte[] tag = new byte[] { (byte)'m', (byte)'v', (byte)'h', (byte)'d' };
                int i = IndexOf(buf, tag);
                if (i < 0 || i + 32 > buf.Length) return 0;
                int version = buf[i + 4];
                if (version == 0)
                {
                    uint timescale = ReadU32(buf, i + 16);
                    uint duration = ReadU32(buf, i + 20);
                    if (timescale == 0) return 0;
                    return (double)duration / timescale;
                }
                else
                {
                    uint timescale = ReadU32(buf, i + 24);
                    ulong duration = ReadU64(buf, i + 28);
                    if (timescale == 0) return 0;
                    return (double)duration / timescale;
                }
            }
            catch { return 0; }
        }

        static int IndexOf(byte[] buf, byte[] pattern)
        {
            int last = buf.Length - pattern.Length;
            for (int i = 0; i <= last; i++)
            {
                bool ok = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (buf[i + j] != pattern[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }

        static uint ReadU32(byte[] b, int i)
        {
            return (uint)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);
        }

        static ulong ReadU64(byte[] b, int i)
        {
            return ((ulong)ReadU32(b, i) << 32) | ReadU32(b, i + 4);
        }

        /// <summary>把大图缩成指定宽度的 jpg 缩略图</summary>
        public static void MakeThumb(string src, string dst, int width)
        {
            using (Image img = Image.FromFile(src))
            {
                int w = width;
                int h = (int)Math.Round((double)img.Height * width / img.Width);
                if (h < 1) h = 1;
                using (Bitmap bmp = new Bitmap(w, h))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(img, 0, 0, w, h);
                    }
                    bmp.Save(dst, ImageFormat.Jpeg);
                }
            }
        }

        /// <summary>追加一条到 index.json，已有同 id 则跳过（幂等）</summary>
        public static void AppendIndex(string root, WallpaperItem it)
        {
            AppendIndex(root, it, "1920x1080");
        }

        /// <summary>同上，但把真实分辨率写进去（B 站轨道可能是 1280x720 等）</summary>
        public static void AppendIndex(string root, WallpaperItem it, string resolution)
        {
            string path = Path.Combine(root, "index.json");
            string text = "[]";
            if (File.Exists(path))
            {
                string raw = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (raw.Length >= 2) text = raw;
            }

            if (text.IndexOf("\"" + it.Id + "\"", StringComparison.Ordinal) >= 0) return;

            StringBuilder sb = new StringBuilder();
            sb.Append("  {\r\n");
            sb.Append("    \"id\": ").Append(Json.Quote(it.Id)).Append(",\r\n");
            sb.Append("    \"title\": ").Append(Json.Quote(it.Title)).Append(",\r\n");
            sb.Append("    \"tag\": ").Append(Json.Quote(it.Tag)).Append(",\r\n");
            sb.Append("    \"file\": ").Append(Json.Quote(it.File)).Append(",\r\n");
            sb.Append("    \"thumb\": ").Append(Json.Quote(it.Thumb)).Append(",\r\n");
            sb.Append("    \"bytes\": ").Append(it.Bytes).Append(",\r\n");
            sb.Append("    \"duration_s\": ").Append(Math.Round(it.Duration, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(",\r\n");
            sb.Append("    \"resolution\": ").Append(Json.Quote(string.IsNullOrEmpty(resolution) ? "1920x1080" : resolution)).Append(",\r\n");
            sb.Append("    \"upgrade_4k\": ").Append(Json.Quote(it.Upgrade4k)).Append("\r\n");
            sb.Append("  }");
            string entry = sb.ToString();

            string body = text.Substring(0, text.Length - 1).TrimEnd();
            string result;
            if (body.EndsWith("[")) result = body + "\r\n" + entry + "\r\n]\r\n";
            else result = body + ",\r\n" + entry + "\r\n]\r\n";

            // 先写 .tmp 再替换，避免写到一半断电把索引写坏
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, result, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);

            // 入库即配文案：下载完这张壁纸就有自己的欢迎语，不必等下次启动再补
            string shot = !string.IsNullOrEmpty(it.ImagePath) ? it.ImagePath : it.ThumbAbs;
            string gl;
            if (Greeting.Ensure(it.Id, it.Title, it.Tag, ColorTag.Of(shot), out gl))
                Log.Note("欢迎语：新入库 " + it.Id + " → 「" + gl + "」");
        }
    }
}
