using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace WallpaperManager
{
    /// <summary>
    /// 标题中文化。
    ///
    /// 分三层，命中越靠前质量越高：
    /// 1. resources\titles-zh.json —— 手写润色过的中文名（不直译，取意境）；
    /// 2. library\zh-cache.json —— 之前联网翻好的结果，落盘复用，断网也有；
    /// 3. 免费接口 MyMemory（实测可用、无需 Key）—— 只兜长尾，译完做一遍去冗余。
    ///
    /// 接口本身是机器翻译，直译痕迹重，所以第 3 层统一走 Polish：
    /// 去掉「动态壁纸/壁纸」这类在卡片上重复的尾缀，去掉中英混排多余空格，
    /// 译不出中文（整句还是英文）就丢弃，宁可显示原名也不显示半成品。
    /// </summary>
    public static class Translate
    {
        static readonly Dictionary<string, string> Map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static readonly object Gate = new object();
        static readonly Queue<string> Pending = new Queue<string>();
        static readonly HashSet<string> Queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string cacheFile;
        static int running;
        static Action<string, string> sink;
        static bool providerDead;

        public static void Init(string dictFile, string runtimeCache)
        {
            cacheFile = runtimeCache;
            LoadMap(dictFile);
            LoadMap(runtimeCache);
        }

        static void LoadMap(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                Dictionary<string, object> d = Json.AsDict(Json.Parse(File.ReadAllText(path, Encoding.UTF8)));
                if (d == null) return;
                lock (Gate)
                {
                    foreach (KeyValuePair<string, object> kv in d)
                    {
                        if (kv.Key.Length > 0 && kv.Key[0] == '_') continue;
                        string v = kv.Value as string;
                        if (string.IsNullOrEmpty(v)) continue;
                        Map[Normalize(kv.Key)] = v;
                    }
                }
            }
            catch { }
        }

        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return string.Join(" ", s.Trim().Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>有中文名就返回，没有返回 null（调用方先显示原名）</summary>
        public static string Get(string origin)
        {
            string k = Normalize(origin);
            if (k.Length == 0) return null;
            lock (Gate)
            {
                string v;
                if (Map.TryGetValue(k, out v)) return v;
            }
            return null;
        }

        /// <summary>纯函数：是否需要翻译（本来就是中文的不动）</summary>
        public static bool NeedsTranslation(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (HasChinese(s)) return false;
            for (int i = 0; i < s.Length; i++)
                if (char.IsLetter(s[i])) return true;
            return false;
        }

        public static bool HasChinese(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 0x4E00 && c <= 0x9FFF) return true;
            }
            return false;
        }

        /// <summary>
        /// 批量补译。命中词典的立即不处理，未命中的排队后台翻，翻好一条回调一条，
        /// 这样卡片先用原名渲染，中文到了再原地替换，不卡界面也不白等。
        /// </summary>
        public static void Prefetch(IEnumerable<string> titles, Action<string, string> onResolved)
        {
            if (titles == null) return;
            lock (Gate)
            {
                if (onResolved != null) sink = onResolved;
                foreach (string t in titles)
                {
                    if (!NeedsTranslation(t)) continue;
                    string k = Normalize(t);
                    if (k.Length == 0) continue;
                    if (Map.ContainsKey(k)) continue;
                    if (Queued.Contains(k)) continue;
                    Queued.Add(k);
                    Pending.Enqueue(k);
                }
                int want = Pending.Count < 4 ? Pending.Count : 4;
                while (running < want)
                {
                    running++;
                    ThreadPool.QueueUserWorkItem(Worker);
                }
            }
        }

        static void Worker(object state)
        {
            while (true)
            {
                string item;
                lock (Gate)
                {
                    if (Pending.Count == 0 || providerDead)
                    {
                        running--;
                        return;
                    }
                    item = Pending.Dequeue();
                }

                string zh = null;
                try { zh = FetchZh(item); }
                catch { }

                lock (Gate)
                {
                    if (zh != null)
                    {
                        Map[item] = zh;
                        SaveMap();
                    }
                    else
                    {
                        Queued.Remove(item);
                    }
                }

                if (zh != null)
                {
                    Action<string, string> cb = sink;
                    if (cb != null)
                    {
                        try { cb(item, zh); }
                        catch { }
                    }
                }
                Thread.Sleep(150);
            }
        }

        static string FetchZh(string en)
        {
            string url = "https://api.mymemory.translated.net/get?q="
                       + Uri.EscapeDataString(en) + "&langpair=en%7Czh-CN&mt=1";
            string text;
            using (WebClient wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0");
                Net.Apply(wc);
                wc.Encoding = Encoding.UTF8;
                text = wc.DownloadString(url);
            }

            Dictionary<string, object> root = Json.AsDict(Json.Parse(text));
            if (root == null) return null;

            long status = Json.Long(root, "responseStatus");
            if (status != 0 && status != 200)
            {
                // 429/403 = 当天免费额度用完，本轮别再打这个接口
                if (status == 429 || status == 403) providerDead = true;
                return null;
            }

            Dictionary<string, object> rd = DictOf(root, "responseData");
            string raw = Json.Str(rd, "translatedText");
            return Polish(en, raw);
        }

        /// <summary>
        /// 纯函数：把机器译文收一遍，去掉直译痕迹。
        /// 返回 null 表示这条不值得用（没译出来 / 仍是英文），调用方保留原名。
        /// </summary>
        public static string Polish(string en, string zh)
        {
            if (string.IsNullOrEmpty(zh)) return null;

            string t = zh.Trim();
            try { t = WebUtility.HtmlDecode(t); }
            catch { }
            t = t.Replace("\r", " ").Replace("\n", " ");

            // 译不出来（整句原样返回英文）就不要
            if (!HasChinese(t)) return null;
            if (string.Equals(Normalize(t), Normalize(en), StringComparison.OrdinalIgnoreCase)) return null;

            // 去掉卡片上本来就会重复的尾缀
            string[] tails = new string[] { "的动态壁纸", "的动态背景", "动态壁纸", "的动态", "壁纸" };
            for (int i = 0; i < tails.Length; i++)
            {
                if (t.EndsWith(tails[i], StringComparison.Ordinal) && t.Length > tails[i].Length + 1)
                {
                    t = t.Substring(0, t.Length - tails[i].Length).Trim();
                    break;
                }
            }

            // 中英混排：中文与拉丁字母/数字之间补一个空格，读起来更清楚
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (c == ' ' || c == '\t')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                    continue;
                }
                if (sb.Length > 0)
                {
                    char prev = sb[sb.Length - 1];
                    bool prevCjk = prev >= 0x4E00 && prev <= 0x9FFF;
                    bool nowLatin = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                    if (prevCjk && nowLatin) sb.Append(' ');
                    if (prev == ' ') sb.Length--;
                }
                sb.Append(c);
            }
            t = sb.ToString().Trim().TrimEnd('。', '，', ',', '.', '的');
            if (!HasChinese(t)) return null;
            return t;
        }

        static void SaveMap()
        {
            if (string.IsNullOrEmpty(cacheFile)) return;
            try
            {
                string dir = Path.GetDirectoryName(cacheFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                StringBuilder sb = new StringBuilder();
                sb.Append("{\r\n");
                bool first = true;
                foreach (KeyValuePair<string, string> kv in Map)
                {
                    if (!first) sb.Append(",\r\n");
                    first = false;
                    sb.Append("  ").Append(Json.Quote(kv.Key)).Append(": ").Append(Json.Quote(kv.Value));
                }
                sb.Append("\r\n}\r\n");

                string tmp = cacheFile + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(cacheFile)) File.Delete(cacheFile);
                File.Move(tmp, cacheFile);
            }
            catch { }
        }

        static Dictionary<string, object> DictOf(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return null;
            return Json.AsDict(v);
        }
    }
}
