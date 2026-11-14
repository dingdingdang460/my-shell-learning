using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WallpaperManager
{
    /// <summary>
    /// 「这张图的相关视频」统一发现器（11-14）。
    ///
    /// 主人的原话：「只要关联的文件夹里有相关图片的视频就应该播放，而不是让我重新生成浪费钱」。
    /// 以前只认 videos\&lt;id&gt;.mp4，后来补了 .bak*，都太窄 —— 主文件被改名、
    /// 出片落在 _raw 子目录、片子跟图片放在同一个文件夹、或者工单里写了别的 out 路径，
    /// 应用都会当成「没生成过」，弹下单浮条，白花一次额度。
    ///
    /// 这里的规则全部只读本机文件，零联网、零费用：
    ///   1) videos\&lt;id&gt;.mp4                              规范主文件
    ///   2) videos\&lt;id&gt;.mp4.bak*                           重出旧片留下的备份
    ///   3) 任何以 id / 图片 stem 开头的视频                  &lt;id&gt;.raw.mp4、&lt;id&gt;-v2.mp4 …
    ///   4) videos\ 下一级子目录里的同名视频；子目录名本身就是 id/图片 stem 时，里面任何视频
    ///   5) 图片自己所在目录、images\、thumbs\、库根下的同 stem 视频
    ///   6) jobs\{queue,done,fail} 工单里 out 字段指向的现存视频（id 或 image 对得上）
    ///   7) 没带 id、但名字一看就是云端服务商原始出片（带 UTC 时间戳）的散片
    ///      —— 11-14 主人实测「文件夹里明明有视频你还让我生成」就是这种；
    ///         按这张图的下单时间就近认领，配不上就取最近出的一片，纯读不复制。
    ///
    /// 1)~6) 找到但不在规范位置时，复制一份到 videos\&lt;id&gt;.mp4（原文件保留），
    /// 让 CloudGen.StateOf / SweepCloudJobs / 播放器这些只认规范路径的老逻辑都不用改。
    /// 7) 是推断关联，只返回原路径直接播，绝不往壁纸库写任何文件；
    /// 把对的片子改名成 &lt;id&gt;.mp4 放进 videos\ 就能覆盖它。
    /// 名称匹配要求 id 后面跟分隔符，避免 &lt;id&gt; 误配到 &lt;id&gt;0 这种同前缀的别的壁纸。
    /// zz- 前缀是本项目自检假条目专用，不参与 7)，以免「到货」类自检被真实库散片误拦。
    /// </summary>
    public static class RelatedVideo
    {
        static readonly object Gate = new object();
        static string CacheRoot = "";
        static DateTime CacheAt = DateTime.MinValue;
        static List<string> CacheFiles = new List<string>();

        /// <summary>本进程内「哪份散片已经归给哪张图」，避免同一份被好几张图抢。</summary>
        static readonly Dictionary<string, string> Claimed =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>自检会连续造/删文件，目录没变但内容变了，这时要手动让缓存失效。</summary>
        public static void Invalidate()
        {
            lock (Gate)
            {
                CacheRoot = "";
                CacheAt = DateTime.MinValue;
                CacheFiles = new List<string>();
                Claimed.Clear();
            }
        }

        static bool IsVideoName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.IndexOf(".mp4", StringComparison.Ordinal) >= 0
                || n.EndsWith(".m4v", StringComparison.Ordinal)
                || n.EndsWith(".mov", StringComparison.Ordinal)
                || n.EndsWith(".wmv", StringComparison.Ordinal)
                || n.EndsWith(".avi", StringComparison.Ordinal);
        }

        static bool IsBackupName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.ToLowerInvariant().IndexOf(".mp4.bak", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// 文件名是否「属于」这个 key。key 后面必须是分隔符，不能是数字/字母
        /// —— biliart-17714251-4 绝不能吃到 biliart-17714251-40 的片子。
        /// </summary>
        static bool NameMatches(string fileName, string key)
        {
            if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(key)) return false;
            string f = fileName.ToLowerInvariant();
            string k = key.ToLowerInvariant();
            if (!f.StartsWith(k, StringComparison.Ordinal)) return false;
            if (f.Length == k.Length) return IsVideoName(f);
            char c = f[k.Length];
            if (c != '.' && c != '-' && c != '_' && c != ' ' && c != '(' && c != '[') return false;
            return IsVideoName(f);
        }

        static string Stem(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetFileNameWithoutExtension(path.Replace('/', '\\')); }
            catch { return ""; }
        }

        static void AddKey(List<string> list, string k)
        {
            if (string.IsNullOrEmpty(k)) return;
            k = k.Trim();
            if (k.Length < 3) return;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], k, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(k);
        }

        /// <summary>一张图可能有的所有名字：条目 id、源图/缩略图/绝对路径的文件名 stem。</summary>
        static List<string> KeysOf(WallpaperItem it, string imagePath)
        {
            List<string> keys = new List<string>();
            AddKey(keys, it.Id);
            AddKey(keys, Stem(it.File));
            AddKey(keys, Stem(it.Thumb));
            AddKey(keys, Stem(it.ImagePath));
            AddKey(keys, Stem(it.ThumbAbs));
            AddKey(keys, Stem(imagePath));
            return keys;
        }

        static string Abs(string libRoot, string rel)
        {
            if (string.IsNullOrEmpty(rel)) return "";
            try
            {
                if (Path.IsPathRooted(rel)) return rel;
                return Path.Combine(libRoot, rel.Replace('/', '\\'));
            }
            catch { return ""; }
        }

        static string DirOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetDirectoryName(path); }
            catch { return ""; }
        }

        static string FolderName(string path)
        {
            try
            {
                string d = Path.GetDirectoryName(path);
                return d == null ? "" : new DirectoryInfo(d).Name;
            }
            catch { return ""; }
        }

        /// <summary>
        /// 只在「当前壁纸库」范围内认片子。换了库目录 / 自检用临时库时，
        /// 不能把上一个库里的视频顺手认进来 —— 那样自检不隔离，生产上也会串片。
        /// </summary>
        static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            try
            {
                string p = Path.GetFullPath(path).TrimEnd('\\', '/');
                string r = Path.GetFullPath(root).TrimEnd('\\', '/');
                return string.Equals(p, r, StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static void AddFiles(List<string> outp, string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                string[] fs = Directory.GetFiles(dir, "*");
                for (int i = 0; i < fs.Length; i++)
                    if (IsVideoName(Path.GetFileName(fs[i]))) outp.Add(fs[i]);
            }
            catch { }
        }

        static void AddSubFiles(List<string> outp, string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                string[] subs = Directory.GetDirectories(dir);
                for (int i = 0; i < subs.Length; i++) AddFiles(outp, subs[i]);
            }
            catch { }
        }

        /// <summary>库内固定几处目录的清单，2 秒缓存：界面刷新会对每张图问一次，别反复扫盘。</summary>
        static List<string> BaseFiles(string libRoot)
        {
            lock (Gate)
            {
                if (string.Equals(CacheRoot, libRoot, StringComparison.OrdinalIgnoreCase)
                    && (DateTime.UtcNow - CacheAt).TotalSeconds < 2.0)
                    return new List<string>(CacheFiles);
            }

            List<string> all = new List<string>();
            string vdir = Path.Combine(libRoot, "videos");
            AddFiles(all, vdir);
            AddSubFiles(all, vdir);
            AddFiles(all, Path.Combine(libRoot, "images"));
            AddFiles(all, Path.Combine(libRoot, "thumbs"));
            AddFiles(all, libRoot);

            lock (Gate)
            {
                CacheRoot = libRoot;
                CacheAt = DateTime.UtcNow;
                CacheFiles = new List<string>(all);
            }
            return all;
        }

        /// <summary>再补上「这张图自己所在的文件夹」以及以它命名的关联子文件夹。</summary>
        static List<string> Collect(string libRoot, WallpaperItem it, string imagePath)
        {
            List<string> all = BaseFiles(libRoot);
            List<string> keys = KeysOf(it, imagePath);
            string[] own = new string[] {
                DirOf(Abs(libRoot, it.File)), DirOf(imagePath),
                DirOf(it.ImagePath), DirOf(it.ThumbAbs)
            };
            for (int i = 0; i < own.Length; i++)
            {
                string d = own[i];
                if (!IsUnder(d, libRoot)) continue;
                AddFiles(all, d);
                for (int k = 0; k < keys.Count; k++)
                {
                    AddFiles(all, Path.Combine(d, keys[k]));
                    AddFiles(all, Path.Combine(Path.Combine(libRoot, "videos"), keys[k]));
                }
            }
            return all;
        }

        /// <summary>候选里挑一份：时间最新，同时间取体积大的（更可能是完整片子而不是残片）。</summary>
        static string Newest(List<string> files, List<string> keys, bool backupsOnly, bool anyInKeyFolder)
        {
            string best = null;
            DateTime bestTime = DateTime.MinValue;
            long bestLen = -1;
            for (int i = 0; i < files.Count; i++)
            {
                string f = files[i];
                string name = Path.GetFileName(f);
                if (backupsOnly && !IsBackupName(name)) continue;

                bool match = false;
                for (int k = 0; k < keys.Count; k++)
                    if (NameMatches(name, keys[k])) { match = true; break; }
                if (!match && anyInKeyFolder)
                {
                    string folder = FolderName(f) + ".mp4";
                    for (int k = 0; k < keys.Count; k++)
                        if (NameMatches(folder, keys[k])) { match = true; break; }
                }
                if (!match) continue;

                DateTime t;
                long len;
                try
                {
                    FileInfo fi = new FileInfo(f);
                    if (!fi.Exists) continue;
                    t = fi.LastWriteTimeUtc;
                    len = fi.Length;
                }
                catch { continue; }
                if (len <= 0) continue;
                if (best == null || t > bestTime || (t == bestTime && len > bestLen))
                {
                    best = f; bestTime = t; bestLen = len;
                }
            }
            return best;
        }

        /// <summary>
        /// 工单里 out 指向的片子：老版本/外部生成器可能把 mp4 落到别的名字，
        /// 只要工单的 id 或 image 跟这张图对得上，就认它是这张的，不重新花钱。
        /// </summary>
        static string FromJobs(string libRoot, WallpaperItem it, string imagePath)
        {
            string[] dirs = new string[] { CloudGen.QueueDir, CloudGen.DoneDir, CloudGen.FailDir };
            string img = string.IsNullOrEmpty(imagePath) ? "" : imagePath.ToLowerInvariant();
            for (int d = 0; d < dirs.Length; d++)
            {
                if (!Directory.Exists(dirs[d])) continue;
                string[] js;
                try { js = Directory.GetFiles(dirs[d], "*.json"); }
                catch { continue; }
                for (int i = 0; i < js.Length; i++)
                {
                    try
                    {
                        Dictionary<string, object> o = Json.AsDict(Json.Parse(File.ReadAllText(js[i], Encoding.UTF8)));
                        if (o == null) continue;
                        string jid = Json.Str(o, "id");
                        string jimg = Json.Str(o, "image");
                        string jout = Json.Str(o, "out");
                        bool sameId = string.Equals(jid, it.Id, StringComparison.OrdinalIgnoreCase);
                        bool sameImg = img.Length > 0 && !string.IsNullOrEmpty(jimg)
                                    && string.Equals(jimg.ToLowerInvariant(), img, StringComparison.Ordinal);
                        if (!sameId && !sameImg) continue;
                        if (string.IsNullOrEmpty(jout) || !IsVideoName(Path.GetFileName(jout))) continue;
                        if (!IsUnder(jout, libRoot)) continue;      // 别的库里的 out 不算
                        FileInfo fo = new FileInfo(jout);
                        if (fo.Exists && fo.Length > 0) return jout;
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>服务商原始出片的名字带 UTC 时间戳：..._2026-11-12T02-18-26.mp4。</summary>
        static readonly System.Text.RegularExpressions.Regex StampRe =
            new System.Text.RegularExpressions.Regex(@"\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}");

        /// <summary>
        /// 「已知名字」全集：历史工单里的 id + 库里所有图片 stem。
        /// 用它把「别的壁纸的片子」排除掉，只留下真正没人认领的原始出片。
        /// 已删掉的壁纸，id 还在 jobs\done 的工单里，所以它的旧片不会被误认成孤儿。
        /// </summary>
        static List<string> KnownKeys(string libRoot)
        {
            List<string> k = new List<string>();
            string[] jdirs = new string[] { CloudGen.QueueDir, CloudGen.DoneDir, CloudGen.FailDir };
            for (int d = 0; d < jdirs.Length; d++)
            {
                if (!Directory.Exists(jdirs[d])) continue;
                string[] js;
                try { js = Directory.GetFiles(jdirs[d], "*.json"); } catch { continue; }
                for (int i = 0; i < js.Length; i++)
                {
                    try
                    {
                        Dictionary<string, object> o =
                            Json.AsDict(Json.Parse(File.ReadAllText(js[i], Encoding.UTF8)));
                        if (o != null) AddKey(k, Json.Str(o, "id"));
                    }
                    catch { }
                }
            }
            string[] picDirs = new string[] {
                Path.Combine(libRoot, "images"), Path.Combine(libRoot, "thumbs"), libRoot
            };
            for (int d = 0; d < picDirs.Length; d++)
            {
                if (!Directory.Exists(picDirs[d])) continue;
                string[] fs;
                try { fs = Directory.GetFiles(picDirs[d]); } catch { continue; }
                for (int i = 0; i < fs.Length; i++)
                {
                    string n = Path.GetFileName(fs[i]).ToLowerInvariant();
                    if (n.EndsWith(".jpg") || n.EndsWith(".jpeg") || n.EndsWith(".png")
                        || n.EndsWith(".webp") || n.EndsWith(".bmp") || n.EndsWith(".gif"))
                        AddKey(k, Stem(fs[i]));
                }
            }
            return k;
        }

        /// <summary>
        /// 只认「没带 id、但一看就是云端出片原始文件名」的散片。
        /// 主人库里那几个 Anime_illustration_of_a_girl_w_2026-11-12T...mp4 就是这种；
        /// 随便一个 totally-unrelated.mp4、或同前缀别的 id 的片子绝不能被误认。
        /// </summary>
        static bool IsOrphan(string file, List<string> known)
        {
            string n = Path.GetFileName(file);
            if (IsBackupName(n)) return false;
            if (!StampRe.IsMatch(n)) return false;
            for (int i = 0; i < known.Count; i++)
                if (NameMatches(n, known[i])) return false;
            return true;
        }

        static long Epoch(DateTime t)
        {
            try
            {
                return (long)(t.ToUniversalTime()
                    - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            }
            catch { return 0; }
        }

        /// <summary>这张图历次下单时间（取最近一次），用来跟原始出片时间就近配对。</summary>
        static long JobCreatedFor(string id)
        {
            long best = 0;
            string[] dirs = new string[] { CloudGen.QueueDir, CloudGen.DoneDir, CloudGen.FailDir };
            for (int d = 0; d < dirs.Length; d++)
            {
                string p = Path.Combine(dirs[d], id + ".json");
                if (!File.Exists(p)) continue;
                try
                {
                    Dictionary<string, object> o =
                        Json.AsDict(Json.Parse(File.ReadAllText(p, Encoding.UTF8)));
                    if (o == null) continue;
                    object raw;
                    if (o.TryGetValue("created", out raw))
                    {
                        long t = Convert.ToInt64(raw);
                        if (t > best) best = t;
                    }
                }
                catch { }
            }
            return best;
        }

        /// <summary>
        /// 最后一层兜底：库里还躺着没带 id 的云端原始出片（服务商原名 + UTC 时间戳）。
        /// 主人 11-14 实测「文件夹里明明有视频，你还要我生成」指的就是这几种。
        /// 先按这张图的下单时间就近配对，配不上就取最近出的一片；
        /// 一份散片本进程内只归一张图。宁可复用，绝不让他再花一次钱。
        /// </summary>
        static string FromOrphans(string libRoot, WallpaperItem it, List<string> files)
        {
            List<string> known = KnownKeys(libRoot);
            long t0 = JobCreatedFor(it.Id);
            const long WindowBefore = 600;        // 出片最多比下单早 10 分钟（时钟误差）
            const long WindowAfter = 12 * 3600;   // 排队 12 小时内回来都算这单

            string best = null, newest = null;
            long bestDelta = long.MaxValue;
            DateTime newestTime = DateTime.MinValue;

            lock (Gate)
            {
                for (int i = 0; i < files.Count; i++)
                {
                    string f = files[i];
                    if (!IsOrphan(f, known)) continue;
                    string owner;
                    if (Claimed.TryGetValue(f, out owner)
                        && !string.Equals(owner, it.Id, StringComparison.OrdinalIgnoreCase)) continue;

                    DateTime mt;
                    long len;
                    try
                    {
                        FileInfo fi = new FileInfo(f);
                        if (!fi.Exists) continue;
                        mt = fi.LastWriteTimeUtc;
                        len = fi.Length;
                    }
                    catch { continue; }
                    if (len <= 0) continue;

                    long ts = Epoch(mt);
                    if (t0 > 0 && ts >= t0 - WindowBefore && ts <= t0 + WindowAfter)
                    {
                        long delta = Math.Abs(ts - t0);
                        if (delta < bestDelta) { bestDelta = delta; best = f; }
                    }
                    if (newest == null || mt > newestTime) { newest = f; newestTime = mt; }
                }
            }
            return best != null ? best : newest;
        }

        static string FindSource(string libRoot, WallpaperItem it, string imagePath, out bool inferred)
        {
            inferred = false;
            if (string.IsNullOrEmpty(libRoot) || it == null || it.IsVideo) return null;
            List<string> keys = KeysOf(it, imagePath);
            if (keys.Count == 0) return null;
            List<string> files = Collect(libRoot, it, imagePath);

            string best = Newest(files, keys, true, false);     // 备份优先，沿用 11-14 的老行为
            if (best != null) return best;
            best = Newest(files, keys, false, false);          // 各种变体命名
            if (best != null) return best;
            best = Newest(files, keys, false, true);            // 以图命名的关联文件夹
            if (best != null) return best;
            best = FromJobs(libRoot, it, imagePath);            // 工单里写死的 out
            if (best != null) return best;

            // zz- 是本项目所有自检假条目的前缀（--arrivetest / --cloudflowtest 等在真实库里
            // 临时造的条目）。它们不该吃到主人库里的散片，否则「到货 / 下单」类自检会被误拦。
            // 真壁纸不会叫这个前缀；新增的 --orphantest 用独立隔离库 + 非 zz- id 专测这一层。
            if (!it.Id.StartsWith("zz-", StringComparison.OrdinalIgnoreCase))
            {
                best = FromOrphans(libRoot, it, files);         // 没带 id 的原始出片
                if (best != null) { inferred = true; return best; }
            }
            return null;
        }

        /// <summary>
        /// 返回可播的规范路径 videos\&lt;id&gt;.mp4；没有任何相关视频时返回 null。
        /// 找到的文件不在规范位置就复制一份过去（原文件保留），并写日志说明来源。
        /// </summary>
        public static string Find(string libRoot, WallpaperItem it, string imagePath)
        {
            if (it == null || it.IsVideo || string.IsNullOrEmpty(it.Id) || string.IsNullOrEmpty(libRoot)) return null;
            string canonical = CloudGen.VideoPath(libRoot, it.Id);
            if (File.Exists(canonical)) return canonical;

            bool inferred = false;
            string src = FindSource(libRoot, it, imagePath, out inferred);
            if (src == null) return null;
            if (string.Equals(src, canonical, StringComparison.OrdinalIgnoreCase)) return canonical;

            // 推断关联的散片：直接播原文件，不复制、不改名、不往壁纸库写任何东西。
            // 只在日志里说清楚来路，主人随时能把对的片子改名成 <id>.mp4 覆盖它。
            if (inferred)
            {
                bool fresh;
                lock (Gate)
                {
                    string owner;
                    fresh = !Claimed.TryGetValue(src, out owner)
                         || !string.Equals(owner, it.Id, StringComparison.OrdinalIgnoreCase);
                    Claimed[src] = it.Id;
                }
                // 界面刷新每秒会问好几次，日志只在第一次认领时写一行，别把日志刷爆。
                if (fresh)
                    Log.Note("飘动视频：按库内未归属的原始出片推断关联 " + it.Id + " ← " + src
                             + "（直接播原文件，不复制、不改动；若不是这张的片子，把对的片子改名成 "
                             + it.Id + ".mp4 放进 videos\\ 即可）");
                return src;
            }

            try
            {
                string dir = Path.GetDirectoryName(canonical);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (!File.Exists(canonical)) File.Copy(src, canonical, false);
                Log.Note("飘动视频：主文件缺失，已把关联视频认作这张的片子 " + it.Id
                         + " ← " + src + "（原文件保留）");
                Invalidate();
                return File.Exists(canonical) ? canonical : null;
            }
            catch (Exception ex)
            {
                Log.Note("!! 飘动视频关联恢复失败 " + it.Id + "：" + ex.Message);
                return null;
            }
        }
    }
}
