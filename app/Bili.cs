using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace WallpaperManager
{
    /// <summary>一条 B 站视频结果（还没入库）</summary>
    public class BiliItem
    {
        public string Bvid = "";
        public string Title = "";
        public string Author = "";
        public int DurationSec;
        public long Play;
        public string PicOrigin = "";   // 原始封面，约 80-250KB，入库时当静态图
        public string PicCard = "";     // 480 宽，卡片用，约 30-60KB
        public string PicPreview = "";  // 1280 宽，放大预览用，约 50-185KB
        public string Page = "";        // 网页地址
    }

    /// <summary>一条 B 站专栏结果（= 一个图集），比视频有用：正文里全是原图</summary>
    public class BiliArticle
    {
        public long Cvid;
        public string Title = "";
        public string Author = "";
        public long View;
        public long Like;
        public string Category = "";
        public List<string> Covers = new List<string>();   // 列表页只给 1-3 张
        public string Page = "";
        public List<string> Images;                        // 点开才拉，正文全部原图
        public List<string> Sizes = new List<string>();    // 与 Images 一一对应，"1754x1240"，空 = 未知
        public int DroppedPortrait;                        // 因为竖版被滤掉的张数
    }

    /// <summary>
    /// B 站搜索与下载。
    ///
    /// 两条实测结论（决定了不需要登录）：
    /// 1. 搜索接口不带 cookie 会返回 412，但随便给一个 buvid3 就放行，不需要账号；
    /// 2. 取播放地址时加 try_look=1，匿名就能拿到 1080P 轨道，不加只有 360P/480P。
    ///    （yt-dlp 能匿名拿 1080P 也是走这个参数）
    ///
    /// 下载必须带 Referer，否则 CDN 返回 403。DASH 轨道是 video-only 的 fMP4，
    /// 没有音轨——动态壁纸不需要声音，反而省一半体积。
    /// </summary>
    public static class Bili
    {
        const string Origin = "https://www.bilibili.com";
        const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";
        const int MinIntervalMs = 600;

        static DateTime lastCall = DateTime.MinValue;
        static readonly object Gate = new object();

        // 之前 WebClient 没设超时（默认 100 秒），请求一挂界面就是纯等待，
        // 看起来像「卡死」。换成 HttpWebRequest 并把超时收到 12 秒。
        static readonly CookieContainer CookieJar = BuildCookieJar();

        static CookieContainer BuildCookieJar()
        {
            CookieContainer c = new CookieContainer();
            try
            {
                // 必须显式写 ".bilibili.com"：CookieContainer 按域名匹配，
                // 只写 www 的话 api.bilibili.com 收不到，搜索接口直接 412。
                // （旧代码是往请求头里硬塞 Cookie 字符串，所以没这个问题）
                Cookie ck1 = new Cookie("buvid3", "1A2B3C4D-1234-5678-9ABC-DEF012345678infoc", "/", ".bilibili.com");
                Cookie ck2 = new Cookie("b_nut", "1700000000", "/", ".bilibili.com");
                c.Add(new Uri(Origin), ck1);
                c.Add(new Uri(Origin), ck2);
            }
            catch { }
            return c;
        }

        static void Throttle()
        {
            lock (Gate)
            {
                int gap = (int)(DateTime.UtcNow - lastCall).TotalMilliseconds;
                if (gap >= 0 && gap < MinIntervalMs) Thread.Sleep(MinIntervalMs - gap);
                lastCall = DateTime.UtcNow;
            }
        }

        static string Get(string url, string referer)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Throttle();
                try
                {
                    return Fetch(url, referer);
                }
                catch (Exception ex)
                {
                    last = ex;
                    Log.Note("GET 失败(第 " + (attempt + 1) + " 次) " + url + " :: " + ex.Message);
                    if (attempt == 0) Thread.Sleep(700);
                }
            }
            throw new Exception("B 站请求失败：" + (last == null ? "未知原因" : last.Message));
        }

        static string Fetch(string url, string referer)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA;
            req.Referer = string.IsNullOrEmpty(referer) ? Origin + "/" : referer;
            req.Accept = "*/*";
            req.CookieContainer = CookieJar;
            req.Timeout = 12000;
            req.ReadWriteTimeout = 15000;
            req.KeepAlive = false;
            Net.Apply(req);                   // 空配置=直连；填了代理就走代理
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9");

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                using (Stream s = resp.GetResponseStream())
                {
                    using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    {
                        string text = sr.ReadToEnd();
                        if (string.IsNullOrEmpty(text))
                            throw new Exception("返回空内容（HTTP " + (int)resp.StatusCode + "）");
                        return text;
                    }
                }
            }
        }

        // ---------------- 搜索 ----------------

        /// <summary>
        /// 搜视频。接口一页最多 20 条，想要更多只能翻页——之前只取一页，
        /// 所以界面上永远只有十几二十条，看着像「B 站就这么点东西」。
        /// </summary>
        public static List<BiliItem> Search(string keyword, int limit)
        {
            if (limit <= 0) limit = 20;
            int pages = (limit + 19) / 20;
            if (pages > 5) pages = 5;       // 翻太多页容易触发风控，5 页 = 100 条够挑了

            List<BiliItem> all = new List<BiliItem>();
            for (int p = 1; p <= pages; p++)
            {
                string url = "https://api.bilibili.com/x/web-interface/search/type?search_type=video"
                           + "&page=" + p.ToString(CultureInfo.InvariantCulture) + "&page_size=20"
                           + "&order=totalrank&keyword=" + Uri.EscapeDataString(keyword);
                List<BiliItem> one = ParseSearch(Get(url, Origin + "/"), 0);

                bool added = false;
                for (int i = 0; i < one.Count; i++)
                {
                    bool dup = false;
                    for (int j = 0; j < all.Count; j++) if (all[j].Bvid == one[i].Bvid) { dup = true; break; }
                    if (dup) continue;
                    all.Add(one[i]);
                    added = true;
                    if (all.Count >= limit) break;
                }
                if (all.Count >= limit || !added) break;
            }
            return all;
        }

        /// <summary>纯函数：解析搜索接口返回，便于脱离网络核对</summary>
        public static List<BiliItem> ParseSearch(string text, int limit)
        {
            List<BiliItem> list = new List<BiliItem>();
            Dictionary<string, object> root = Json.AsDict(Json.Parse(text));
            if (root == null) throw new Exception("B 站返回内容无法解析");

            long code = Json.Long(root, "code");
            if (code != 0)
                throw new Exception("B 站接口返回 " + code + "：" + Json.Str(root, "message"));

            Dictionary<string, object> data = DictOf(root, "data");
            object[] arr = data == null ? null : Json.AsArr(Get(data, "result"));
            if (arr == null) return list;

            for (int i = 0; i < arr.Length; i++)
            {
                Dictionary<string, object> d = Json.AsDict(arr[i]);
                if (d == null) continue;

                string bv = Json.Str(d, "bvid");
                if (bv.Length == 0) continue;

                BiliItem it = new BiliItem();
                it.Bvid = bv;
                it.Title = CleanTitle(Json.Str(d, "title"));
                it.Author = Json.Str(d, "author");
                it.Play = Json.Long(d, "play");
                it.DurationSec = ParseDuration(Json.Str(d, "duration"));

                string pic = Json.Str(d, "pic");
                if (pic.StartsWith("//", StringComparison.Ordinal)) pic = "https:" + pic;
                it.PicOrigin = pic;
                it.PicCard = pic.Length == 0 ? "" : pic + "@480w_270h_1c.jpg";
                it.PicPreview = pic.Length == 0 ? "" : pic + "@1280w_720h_1c.jpg";
                it.Page = Origin + "/video/" + bv;

                list.Add(it);
                if (limit > 0 && list.Count >= limit) break;
            }
            return list;
        }

        /// <summary>纯函数：去掉搜索高亮标签 &lt;em class="keyword"&gt; 与 HTML 实体</summary>
        public static string CleanTitle(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string t = Regex.Replace(raw, "<[^>]+>", "");
            try { t = WebUtility.HtmlDecode(t); }
            catch { }
            return t.Trim();
        }

        /// <summary>纯函数："1:41" / "20:7" / "1:02:03" 转秒</summary>
        public static int ParseDuration(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            string[] parts = s.Trim().Split(':');
            int total = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                int v;
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return 0;
                total = total * 60 + v;
            }
            return total;
        }

        // ---------------- 取最高可用视频轨 ----------------

        /// <summary>
        /// 解析出可下载的视频直链。匿名上限是 1080P（try_look=1），
        /// 同分辨率下优先 AVC(H.264)——WPF/媒体基础解码最稳，HEVC/AV1 需要额外解码器。
        /// </summary>
        public static string ResolveBestVideo(string bvid, out int outW, out int outH)
        {
            outW = 0;
            outH = 0;

            string pageRef = Origin + "/video/" + bvid;
            string viewText = Get("https://api.bilibili.com/x/web-interface/view?bvid=" + bvid, pageRef);
            Dictionary<string, object> view = Json.AsDict(Json.Parse(viewText));
            if (view == null) throw new Exception("B 站视频信息无法解析");

            long vcode = Json.Long(view, "code");
            if (vcode != 0)
                throw new Exception("B 站视频信息返回 " + vcode + "：" + Json.Str(view, "message"));

            Dictionary<string, object> vd = DictOf(view, "data");
            long cid = Json.Long(vd, "cid");
            if (cid <= 0) throw new Exception("这个视频没有可下载的分片（可能是番剧、直播或需要登录的内容）");

            string pu = "https://api.bilibili.com/x/player/playurl?bvid=" + bvid + "&cid="
                      + cid.ToString(CultureInfo.InvariantCulture)
                      + "&qn=120&fnval=4048&fourk=1&try_look=1";
            Dictionary<string, object> p = Json.AsDict(Json.Parse(Get(pu, pageRef)));
            if (p == null) throw new Exception("B 站播放地址无法解析");

            long pcode = Json.Long(p, "code");
            if (pcode != 0)
                throw new Exception("B 站播放地址返回 " + pcode + "：" + Json.Str(p, "message"));

            Dictionary<string, object> pdata = DictOf(p, "data");
            Dictionary<string, object> dash = DictOf(pdata, "dash");
            object[] videos = dash == null ? null : Json.AsArr(Get(dash, "video"));
            if (videos == null || videos.Length == 0)
                throw new Exception("B 站没有返回可下载轨道（这个视频可能只能在线播放）");

            string bestUrl = "";
            int bestScore = -1;
            int bestW = 0;
            int bestH = 0;
            int fallbackScore = int.MaxValue;
            string fallbackUrl = "";
            int fallbackW = 0;
            int fallbackH = 0;

            for (int i = 0; i < videos.Length; i++)
            {
                Dictionary<string, object> e = Json.AsDict(videos[i]);
                if (e == null) continue;

                string url = Json.Str(e, "baseUrl");
                if (url.Length == 0) url = Json.Str(e, "base_url");
                if (url.Length == 0) continue;

                int h = (int)Json.Long(e, "height");
                int w = (int)Json.Long(e, "width");
                int codecid = (int)Json.Long(e, "codecid");
                bool avc = codecid == 7;

                // 高分屏也能用：优先不超过 1080P 的最高轨，同等分辨率优先 AVC
                if (h > 0 && h <= 1080)
                {
                    int score = h * 10 + (avc ? 5 : 0);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestUrl = url;
                        bestW = w;
                        bestH = h;
                    }
                }
                if (h > 0 && h < fallbackScore)
                {
                    fallbackScore = h;
                    fallbackUrl = url;
                    fallbackW = w;
                    fallbackH = h;
                }
            }

            if (bestUrl.Length == 0)
            {
                bestUrl = fallbackUrl;
                bestW = fallbackW;
                bestH = fallbackH;
            }
            if (bestUrl.Length == 0) throw new Exception("没有解析到可用的视频地址");

            outW = bestW;
            outH = bestH;
            return bestUrl;
        }

        // ---------------- 专栏（图集）：静态壁纸的主来源 ----------------
        //
        // 为什么不搜视频：视频封面就一张缩略图，内容还得下 10MB 才知道是什么。
        // 专栏（read）正文本身就是几十张原图，一篇顶一个图集，最适合挑静态壁纸。
        // 接口实测：一页 20 条、numResults 常上千；详情 x/article/view 的 content
        // 里是 <img src="//i0.hdslb.com/bfs/article/xxx.png">，带 Referer 直接能下原图。

        /// <summary>一条 B 站专栏结果（= 一个图集）</summary>

        public static List<BiliArticle> SearchArticles(string keyword, int pages)
        {
            if (pages <= 0) pages = 1;

            List<BiliArticle> all = new List<BiliArticle>();
            for (int p = 1; p <= pages; p++)
            {
                string url = "https://api.bilibili.com/x/web-interface/search/type?search_type=article"
                           + "&page=" + p.ToString(CultureInfo.InvariantCulture) + "&page_size=20"
                           + "&order=totalrank&keyword=" + Uri.EscapeDataString(keyword);
                List<BiliArticle> one = ParseArticles(Get(url, Origin + "/read/"));

                bool added = false;
                for (int i = 0; i < one.Count; i++)
                {
                    bool dup = false;
                    for (int j = 0; j < all.Count; j++) if (all[j].Cvid == one[i].Cvid) { dup = true; break; }
                    if (dup) continue;
                    all.Add(one[i]);
                    added = true;
                }
                if (!added) break;      // 后面几页全是重复的，别再空跑请求
            }
            return all;
        }

        /// <summary>纯函数：解析专栏列表，没配图的直接丢掉（对壁纸没用）</summary>
        public static List<BiliArticle> ParseArticles(string text)
        {
            List<BiliArticle> list = new List<BiliArticle>();
            Dictionary<string, object> root = Json.AsDict(Json.Parse(text));
            if (root == null) throw new Exception("B 站专栏结果无法解析");

            long code = Json.Long(root, "code");
            if (code != 0)
                throw new Exception("B 站专栏接口返回 " + code + "：" + Json.Str(root, "message"));

            Dictionary<string, object> data = DictOf(root, "data");
            object[] arr = data == null ? null : Json.AsArr(Get(data, "result"));
            if (arr == null) return list;

            for (int i = 0; i < arr.Length; i++)
            {
                Dictionary<string, object> d = Json.AsDict(arr[i]);
                if (d == null) continue;

                long id = Json.Long(d, "id");
                if (id <= 0) continue;

                BiliArticle a = new BiliArticle();
                a.Cvid = id;
                a.Title = CleanTitle(Json.Str(d, "title"));
                a.Author = Json.Str(d, "author");
                a.View = Json.Long(d, "view");
                a.Like = Json.Long(d, "like");
                a.Category = Json.Str(d, "category_name");
                a.Page = Origin + "/read/cv" + id.ToString(CultureInfo.InvariantCulture);

                object[] imgs = Json.AsArr(Get(d, "image_urls"));
                if (imgs != null)
                {
                    for (int k = 0; k < imgs.Length; k++) AddImage(a.Covers, imgs[k] as string);
                }
                if (a.Covers.Count == 0) AddImage(a.Covers, Json.Str(d, "banner_url"));
                if (a.Covers.Count == 0) continue;

                list.Add(a);
            }
            return list;
        }

        /// <summary>
        /// 拉一篇专栏正文里的全部原图地址。
        ///
        /// 昨天点开就卡在「正在读取…」，其实不是网络卡住，而是失败信息被预览层挡住了。
        /// 现在四路兜底，每一路单独记日志，全失败也返回封面并在 note 里说清楚：
        ///   ① x/article/view 的 content（HTML 正文，+尺寸）
        ///   ② data.opus.content.paragraphs[].pic.pics[]（opus 型专栏图片全在这里）
        ///   ③ image_urls / origin_image_urls（列表型字段）
        ///   ④ 直接抓 /read/cv&lt;id&gt; 网页 HTML
        /// </summary>
        public static List<string> LoadArticleImages(BiliArticle a, out string note)
        {
            note = "";
            List<string> list = new List<string>();
            List<string> sizes = new List<string>();
            if (a == null) { note = "专栏数据为空"; return list; }

            string idText = a.Cvid.ToString(CultureInfo.InvariantCulture);
            string pageRef = Origin + "/read/cv" + idText;

            // ① + ② + ③：一次接口请求，内部依次尝试
            try
            {
                string text = Get("https://api.bilibili.com/x/article/view?id=" + idText, pageRef);
                ParseArticleDetail(text, list, sizes);
                Log.Note("图集 " + idText + "：接口解析出 " + list.Count + " 张");
            }
            catch (Exception ex)
            {
                Log.Note("图集 " + idText + "：接口失败 " + ex.Message);
            }

            // ④：网页兜底
            if (list.Count == 0)
            {
                try
                {
                    string html = Get(pageRef, Origin + "/read/");
                    ParseArticleHtml(html, list, sizes);
                    Log.Note("图集 " + idText + "：网页解析出 " + list.Count + " 张");
                }
                catch (Exception ex)
                {
                    Log.Note("图集 " + idText + "：网页失败 " + ex.Message);
                }
            }

            // ⑤：只剩封面也比什么都没有强，但要说清楚
            if (list.Count == 0 && a.Covers != null && a.Covers.Count > 0)
            {
                for (int i = 0; i < a.Covers.Count; i++)
                {
                    Push(list, sizes, a.Covers[i], 0, 0);
                }
                note = "正文没读到，只能拿到列表页这 " + list.Count + " 张（点「在浏览器打开」可以看原帖）";
                Log.Note("图集 " + idText + "：退到封面 " + list.Count + " 张");
            }

            if (list.Count == 0) note = "这篇专栏里没有可用图片";

            a.Images = list;
            a.Sizes = sizes;
            return list;
        }

        /// <summary>纯函数：抽正文 &lt;img src/data-src&gt;，兼容旧调用</summary>
        public static List<string> ParseArticleImages(string text)
        {
            List<string> list = new List<string>();
            List<string> sizes = new List<string>();
            ParseArticleDetail(text, list, sizes);
            return list;
        }

        static void ParseArticleDetail(string text, List<string> list, List<string> sizes)
        {
            Dictionary<string, object> root = Json.AsDict(Json.Parse(text));
            if (root == null)
            {
                Log.Note("详情 JSON 解析不出对象（原文 " + text.Length + " 字节）");
                return;
            }

            long code = Json.Long(root, "code");
            if (code != 0)
            {
                Log.Note("详情接口 code=" + code + " msg=" + Json.Str(root, "message"));
                return;
            }

            Dictionary<string, object> data = DictOf(root, "data");
            if (data == null) return;

            string content = Json.Str(data, "content");
            if (content.Length > 0) ParseImgTags(content, list, sizes);

            if (list.Count == 0) ParseOpus(data, list, sizes);
            if (list.Count == 0) ParseUrlArray(data, "image_urls", list, sizes);
            if (list.Count == 0) ParseUrlArray(data, "origin_image_urls", list, sizes);
        }

        static readonly Regex ImgTagRx = new Regex("<img\\b[^>]*>", RegexOptions.IgnoreCase);
        static readonly Regex SrcAttrRx = new Regex("(?:data-src|src)\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase);
        static readonly Regex WidthRx = new Regex("\\bwidth\\s*=\\s*[\"']?(\\d+)", RegexOptions.IgnoreCase);
        static readonly Regex HeightRx = new Regex("\\bheight\\s*=\\s*[\"']?(\\d+)", RegexOptions.IgnoreCase);
        static readonly Regex RawBfsRx = new Regex(
            "(?:https?:)?//[a-z0-9.\\-]*hdslb\\.com/bfs/(?:article|new_dyn)/[A-Za-z0-9_\\-]+(?:\\.(?:jpg|jpeg|png|webp))",
            RegexOptions.IgnoreCase);

        static void ParseImgTags(string html, List<string> list, List<string> sizes)
        {
            if (string.IsNullOrEmpty(html)) return;
            MatchCollection tags = ImgTagRx.Matches(html);
            for (int i = 0; i < tags.Count; i++)
            {
                string tag = tags[i].Value;
                Match src = SrcAttrRx.Match(tag);
                if (!src.Success) continue;
                int w = 0;
                int h = 0;
                Match mw = WidthRx.Match(tag);
                if (mw.Success) int.TryParse(mw.Groups[1].Value, out w);
                Match mh = HeightRx.Match(tag);
                if (mh.Success) int.TryParse(mh.Groups[1].Value, out h);
                Push(list, sizes, src.Groups[1].Value, w, h);
            }
        }

        static void ParseOpus(Dictionary<string, object> data, List<string> list, List<string> sizes)
        {
            Dictionary<string, object> opus = DictOf(data, "opus");
            if (opus == null) return;
            Dictionary<string, object> content = DictOf(opus, "content");
            if (content == null) return;
            object[] paras = Json.AsArr(Get(content, "paragraphs"));
            if (paras == null) return;

            for (int i = 0; i < paras.Length; i++)
            {
                Dictionary<string, object> p = Json.AsDict(paras[i]);
                if (p == null) continue;
                Dictionary<string, object> pic = DictOf(p, "pic");
                if (pic == null) continue;
                object[] pics = Json.AsArr(Get(pic, "pics"));
                if (pics == null) continue;
                for (int k = 0; k < pics.Length; k++)
                {
                    Dictionary<string, object> o = Json.AsDict(pics[k]);
                    if (o == null) continue;
                    Push(list, sizes, Json.Str(o, "url"), (int)Json.Long(o, "width"), (int)Json.Long(o, "height"));
                }
            }
        }

        static void ParseUrlArray(Dictionary<string, object> data, string key, List<string> list, List<string> sizes)
        {
            object[] arr = Json.AsArr(Get(data, key));
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++) Push(list, sizes, arr[i] as string, 0, 0);
        }

        static void ParseArticleHtml(string html, List<string> list, List<string> sizes)
        {
            if (string.IsNullOrEmpty(html)) return;
            ParseImgTags(html, list, sizes);
            if (list.Count > 0) return;
            MatchCollection ms = RawBfsRx.Matches(html);
            for (int i = 0; i < ms.Count; i++) Push(list, sizes, ms[i].Value, 0, 0);
        }

        /// <summary>收图：归一化 URL、屏蔽表情/图标、去重，并同步记录尺寸</summary>
        static void Push(List<string> list, List<string> sizes, string raw, int w, int h)
        {
            string u = NormalizeUrl(raw);
            if (u.Length == 0) return;

            string low = u.ToLowerInvariant();
            if (low.EndsWith(".gif")) return;                                   // 动图当不了静态壁纸
            if (low.IndexOf("/bfs/h5/", StringComparison.Ordinal) >= 0) return;  // 站内表情/图标
            if (low.IndexOf("/bfs/article/", StringComparison.Ordinal) < 0
                && low.IndexOf("/bfs/new_dyn/", StringComparison.Ordinal) < 0) return;

            for (int i = 0; i < list.Count; i++) if (list[i] == u) return;
            list.Add(u);
            sizes.Add(w > 0 && h > 0 ? w + "x" + h : "");
        }

        static void AddImage(List<string> list, string raw)
        {
            string u = NormalizeUrl(raw);
            if (u.Length == 0) return;

            string low = u.ToLowerInvariant();
            if (low.EndsWith(".gif")) return;                                  // 动图当不了静态壁纸
            if (low.IndexOf("/bfs/h5/", StringComparison.Ordinal) >= 0) return; // 站内表情/图标
            if (low.IndexOf("/bfs/article/", StringComparison.Ordinal) < 0
                && low.IndexOf("/bfs/new_dyn/", StringComparison.Ordinal) < 0) return;

            for (int i = 0; i < list.Count; i++) if (list[i] == u) return;
            list.Add(u);
        }

        public static string NormalizeUrl(string u)
        {
            if (string.IsNullOrEmpty(u)) return "";
            u = u.Trim();
            if (u.StartsWith("//", StringComparison.Ordinal)) return "https:" + u;
            if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "https://" + u.Substring(7);
            return u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? u : "";
        }

        /// <summary>去掉 CDN 的 @1280w 之类的处理参数，拿回原图地址</summary>
        public static string StripVariant(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int at = url.LastIndexOf('@');
            return at > 8 ? url.Substring(0, at) : url;
        }

        /// <summary>按 CDN 参数取规格：卡片 364w_205h_1c / 预览 1280w / 入库 1920w</summary>
        public static string Variant(string url, string spec)
        {
            string b = StripVariant(url);
            return b.Length == 0 ? "" : b + "@" + spec;
        }

        /// <summary>下载图集里的一张，转成 1920 宽 JPG 入库（原图有的 3MB，费流量也没必要）</summary>
        public static WallpaperItem InstallArticleImage(BiliArticle a, string rawUrl, int index, int total,
                                                       string root, string tag, Action<string> progress)
        {
            if (a == null) throw new ArgumentNullException("a");
            if (string.IsNullOrEmpty(rawUrl)) throw new Exception("图片地址为空");
            if (string.IsNullOrEmpty(root)) throw new Exception("壁纸库路径为空");

            string id = "biliart-" + a.Cvid.ToString(CultureInfo.InvariantCulture)
                      + "-" + (index + 1).ToString(CultureInfo.InvariantCulture);
            string dirImages = Path.Combine(root, "images");
            string dirThumbs = Path.Combine(root, "thumbs");
            Directory.CreateDirectory(dirImages);
            Directory.CreateDirectory(dirThumbs);

            string imgAbs = Path.Combine(dirImages, id + ".jpg");
            string thumbAbs = Path.Combine(dirThumbs, id + ".jpg");

            if (!File.Exists(imgAbs) || new FileInfo(imgAbs).Length < 15000)
            {
                if (progress != null)
                    progress("正在下载第 " + (index + 1) + "/" + total + " 张：" + a.Title);
                DownloadTo(Variant(rawUrl, "1920w.jpg"), imgAbs, 15000, Origin + "/");
            }

            if (!File.Exists(thumbAbs))
            {
                try { Online.MakeThumb(imgAbs, thumbAbs, 560); }
                catch { }
            }

            WallpaperItem it = new WallpaperItem();
            it.Id = id;
            it.Title = total > 1 ? a.Title + "（第 " + (index + 1) + " 张）" : a.Title;
            it.Tag = string.IsNullOrEmpty(tag) ? "anime" : tag;
            it.File = "images/" + id + ".jpg";
            it.Thumb = File.Exists(thumbAbs) ? "thumbs/" + id + ".jpg" : "images/" + id + ".jpg";
            it.Bytes = new FileInfo(imgAbs).Length;
            it.Duration = 0;
            it.Upgrade4k = "";
            it.IsVideo = false;
            it.ThumbAbs = File.Exists(thumbAbs) ? thumbAbs : imgAbs;
            it.ImagePath = imgAbs;

            Online.AppendIndex(root, it, "1920x1080");
            return it;
        }

        // ---------------- 入库 ----------------

        /// <summary>
        /// 下载并登记：视频进 videos/、封面进 images/、缩略图进 thumbs/，最后追加 index.json。
        /// 文件名用 bvid，安全且天然去重（同一视频重复下载会跳过）。
        /// </summary>
        public static WallpaperItem Install(BiliItem b, string root, string tag, Action<string> progress)
        {
            if (b == null) throw new ArgumentNullException("b");
            if (string.IsNullOrEmpty(root)) throw new Exception("壁纸库路径为空");

            string id = "bili-" + b.Bvid;
            string dirVideos = Path.Combine(root, "videos");
            string dirImages = Path.Combine(root, "images");
            string dirThumbs = Path.Combine(root, "thumbs");
            Directory.CreateDirectory(dirVideos);
            Directory.CreateDirectory(dirImages);
            Directory.CreateDirectory(dirThumbs);

            string videoAbs = Path.Combine(dirVideos, id + ".mp4");
            string coverAbs = Path.Combine(dirImages, id + ".jpg");
            string thumbAbs = Path.Combine(dirThumbs, id + ".jpg");
            string pageRef = Origin + "/video/" + b.Bvid;

            int w = 0;
            int h = 0;
            if (!File.Exists(videoAbs) || new FileInfo(videoAbs).Length < 100000)
            {
                if (progress != null) progress("正在解析 B 站视频地址：" + b.Title);
                string url = ResolveBestVideo(b.Bvid, out w, out h);

                if (progress != null)
                    progress("正在下载 " + (h > 0 ? h + "P" : "视频") + "：" + b.Title);
                DownloadTo(url, videoAbs, 100000, pageRef);
            }
            else
            {
                h = 1080;
                w = 1920;
            }

            if (!File.Exists(coverAbs) || new FileInfo(coverAbs).Length < 8000)
            {
                if (progress != null) progress("正在下载封面：" + b.Title);
                try { DownloadTo(b.PicOrigin, coverAbs, 8000, Origin + "/"); }
                catch { /* 封面失败不阻塞入库 */ }
            }

            if (!File.Exists(thumbAbs))
            {
                try
                {
                    if (File.Exists(coverAbs)) Online.MakeThumb(coverAbs, thumbAbs, 560);
                }
                catch { }
            }

            WallpaperItem it = new WallpaperItem();
            it.Id = id;
            it.Title = b.Title;
            it.Tag = string.IsNullOrEmpty(tag) ? "bili" : tag;
            it.File = "videos/" + id + ".mp4";
            it.Thumb = File.Exists(thumbAbs) ? "thumbs/" + id + ".jpg" : (File.Exists(coverAbs) ? "images/" + id + ".jpg" : "");
            it.Bytes = new FileInfo(videoAbs).Length;
            it.Duration = b.DurationSec > 0 ? b.DurationSec : Online.Mp4DurationSeconds(videoAbs);
            it.Upgrade4k = "";
            it.IsVideo = true;
            it.ThumbAbs = File.Exists(thumbAbs) ? thumbAbs : (File.Exists(coverAbs) ? coverAbs : "");
            it.ImagePath = File.Exists(coverAbs) ? coverAbs : null;

            string res = (w > 0 && h > 0) ? w + "x" + h : "1920x1080";
            Online.AppendIndex(root, it, res);
            return it;
        }

        static void DownloadTo(string url, string target, long minBytes, string referer)
        {
            if (string.IsNullOrEmpty(url)) throw new Exception("下载地址为空");
            string tmp = target + ".part";
            try
            {
                // 同样不用 WebClient：默认 100 秒才超时，断网时会一直挂着
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = UA;
                req.Referer = string.IsNullOrEmpty(referer) ? Origin + "/" : referer;
                req.Timeout = 20000;
                req.ReadWriteTimeout = 30000;
                req.KeepAlive = false;
                Net.Apply(req);

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    using (Stream s = resp.GetResponseStream())
                    {
                        using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            byte[] buf = new byte[65536];
                            while (true)
                            {
                                int n = s.Read(buf, 0, buf.Length);
                                if (n <= 0) break;
                                fs.Write(buf, 0, n);
                            }
                        }
                    }
                }

                long len = new FileInfo(tmp).Length;
                if (len < minBytes)
                    throw new Exception("下载内容过小（" + len + " 字节），可能不是有效文件");
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        // ---------------- JSON 小工具 ----------------
        // JavaScriptSerializer 把对象解析成 Dictionary<string,object>、数组解析成 object[]，
        // 深层取值每层都要判空，这里收成两个辅助函数。

        public static Dictionary<string, object> DictOf(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return null;
            return Json.AsDict(v);
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(key, out v)) return null;
            return v;
        }
    }
}
