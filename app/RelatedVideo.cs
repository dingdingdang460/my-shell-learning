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
    ///   7) 名字完全对不上的散片 —— **只有画面确实是同一张图才认**（见 VideoFingerprint）
    ///
    /// 1)~6) 找到但不在规范位置时，复制一份到 videos\&lt;id&gt;.mp4（原文件保留），
    /// 让 CloudGen.StateOf / SweepCloudJobs / 播放器这些只认规范路径的老逻辑都不用改。
    /// 7) 是内容判定：抽视频首帧与源图做归一化灰度 MAD + dHash 双比对，
    ///    全库按距离升序贪心分配，一段片子只归一张图、一张图只拿一段片子；
    ///    直接播原文件，不复制不改名不往壁纸库写任何文件。
    ///
    /// 11-14 深夜的重要教训：第 7 层最早是「按下单时间就近认领」，结果把 11-12
    /// 别的壁纸的原始出片认给了 11-14 刚下载的新图，主人点「动态」就播到别的壁纸去了。
    /// 主人明确要求：「不要就近认领……你要做到区分图片与视频是否为同一张图片背景」。
    /// 所以时间、文件名、下单记录一概不作为归属证据，只认画面。
    ///
    /// 名称匹配要求 id 后面跟分隔符，避免 &lt;id&gt; 误配到 &lt;id&gt;0 这种同前缀的别的壁纸。
    /// zz- 前缀是本项目自检假条目专用，不参与 7)，以免「到货」类自检被真实库散片误拦。
    /// </summary>
    public static class RelatedVideo
    {
        static readonly object Gate = new object();
        static string CacheRoot = "";
        static DateTime CacheAt = DateTime.MinValue;
        static List<string> CacheFiles = new List<string>();

        /// <summary>
        /// 内容比对结果：图片 stem → 被认领的视频路径。签名（文件 mtime+size）变了才重算，
        /// 界面每次刷新都会问一遍 HasCloudVideo，不能反复解码视频。
        /// </summary>
        static readonly Dictionary<string, string> ContentMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> ContentLogged = new HashSet<string>();
        static string ContentSig = "\u0000none";

        /// <summary>自检会连续造/删文件，目录没变但内容变了，这时要手动让缓存失效。</summary>
        public static void Invalidate()
        {
            lock (Gate)
            {
                CacheRoot = "";
                CacheAt = DateTime.MinValue;
                CacheFiles = new List<string>();
                ContentMap.Clear();
                ContentLogged.Clear();
                ContentSig = "\u0000none";
            }
            VideoFingerprint.Invalidate();
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

        static bool HasPath(List<string> list, string p)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void AddFiles(List<string> outp, string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                string[] fs = Directory.GetFiles(dir, "*");
                for (int i = 0; i < fs.Length; i++)
                    if (IsVideoName(Path.GetFileName(fs[i])) && !HasPath(outp, fs[i])) outp.Add(fs[i]);
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

        /// <summary>
        /// 「已知名字」全集：历史工单里的 id + 库里所有图片 stem。
        /// 用它把「名字上已经明确归给某张图」的片子排除掉，只留下真正没人认领的散片。
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

        /// <summary>名字不匹配任何已知 id / 图片 stem 的视频，才需要靠画面认归属。</summary>
        static List<string> UnownedVideos(string libRoot, List<string> files)
        {
            List<string> known = KnownKeys(libRoot);
            List<string> cand = new List<string>();
            for (int i = 0; i < files.Count; i++)
            {
                string f = files[i];
                if (!IsUnder(f, libRoot)) continue;
                string n = Path.GetFileName(f);
                if (IsBackupName(n)) continue;
                bool owned = false;
                for (int k = 0; k < known.Count; k++)
                    if (NameMatches(n, known[k])) { owned = true; break; }
                if (!owned && !HasPath(cand, f)) cand.Add(f);
            }
            return cand;
        }

        /// <summary>库里可当比对基准的图片：images\ 优先，其次库根，缩略图只补漏。</summary>
        static List<string> Pictures(string libRoot)
        {
            List<string> p = new List<string>();
            List<string> keys = new List<string>();
            string[] dirs = new string[] {
                Path.Combine(libRoot, "images"), libRoot, Path.Combine(libRoot, "thumbs")
            };
            for (int d = 0; d < dirs.Length; d++)
            {
                if (!Directory.Exists(dirs[d])) continue;
                string[] fs;
                try { fs = Directory.GetFiles(dirs[d]); } catch { continue; }
                for (int i = 0; i < fs.Length; i++)
                {
                    string n = Path.GetFileName(fs[i]).ToLowerInvariant();
                    if (!(n.EndsWith(".jpg") || n.EndsWith(".jpeg") || n.EndsWith(".png")
                       || n.EndsWith(".webp") || n.EndsWith(".bmp") || n.EndsWith(".gif"))) continue;
                    string key = Stem(fs[i]).ToLowerInvariant();
                    if (keys.Contains(key)) continue;
                    keys.Add(key);
                    p.Add(fs[i]);
                }
            }
            return p;
        }

        static string Signature(List<string> files)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < files.Count; i++)
            {
                try
                {
                    FileInfo fi = new FileInfo(files[i]);
                    if (!fi.Exists) continue;
                    sb.Append(files[i].ToLowerInvariant()).Append(':')
                      .Append(fi.LastWriteTimeUtc.Ticks).Append(':')
                      .Append(fi.Length).Append('|');
                }
                catch { }
            }
            return sb.ToString();
        }

        sealed class Pair
        {
            public int Pic, Vid;
            public double Mad;
            public int Hash;
        }

        /// <summary>
        /// 第 7 层的核心：全库一次性算「每张图 × 每段未归属视频」的内容距离，
        /// 按 MAD 升序贪心分配，一段片子只归一张图、一张图只拿一段片子。
        /// 只有画面确实对得上（MAD 与 dHash 双阈值）才认，绝不按时间/名字猜。
        /// </summary>
        static void EnsureContentMap(string libRoot)
        {
            List<string> files = BaseFiles(libRoot);
            List<string> cand = UnownedVideos(libRoot, files);
            List<string> pics = Pictures(libRoot);
            string sig = Signature(pics) + "#" + Signature(cand);

            lock (Gate)
            {
                if (string.Equals(sig, ContentSig, StringComparison.Ordinal)) return;
            }

            VideoFingerprint.Fp[] pf = new VideoFingerprint.Fp[pics.Count];
            VideoFingerprint.Fp[] vf = new VideoFingerprint.Fp[cand.Count];
            for (int i = 0; i < pics.Count; i++) pf[i] = VideoFingerprint.Image(pics[i]);
            for (int j = 0; j < cand.Count; j++) vf[j] = VideoFingerprint.Video(cand[j]);

            List<Pair> pairs = new List<Pair>();
            for (int i = 0; i < pics.Count; i++)
                for (int j = 0; j < cand.Count; j++)
                {
                    int hd;
                    double mad = VideoFingerprint.Compare(pf[i], vf[j], out hd);
                    if (VideoFingerprint.Same(mad, hd))
                    {
                        Pair q = new Pair();
                        q.Pic = i; q.Vid = j; q.Mad = mad; q.Hash = hd;
                        pairs.Add(q);
                    }
                }
            pairs.Sort(delegate(Pair a, Pair b) { return a.Mad.CompareTo(b.Mad); });

            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool[] usedP = new bool[pics.Count];
            bool[] usedV = new bool[cand.Count];
            StringBuilder detail = new StringBuilder();
            for (int k = 0; k < pairs.Count; k++)
            {
                Pair q = pairs[k];
                if (usedP[q.Pic] || usedV[q.Vid]) continue;
                usedP[q.Pic] = true;
                usedV[q.Vid] = true;
                string key = Stem(pics[q.Pic]);
                map[key] = cand[q.Vid];
                detail.Append("  ").Append(key).Append(" ← ")
                      .Append(Path.GetFileName(cand[q.Vid]))
                      .Append("  MAD=").Append(q.Mad.ToString("0.000"))
                      .Append(" hash=").Append(q.Hash).Append("\r\n");
            }

            lock (Gate)
            {
                ContentMap.Clear();
                foreach (KeyValuePair<string, string> kv in map) ContentMap[kv.Key] = kv.Value;
                ContentSig = sig;
            }
            Log.Note("飘动视频：内容比对 " + pics.Count + " 张图 × " + cand.Count
                     + " 段未归属视频 → 认领 " + map.Count + " 对（画面不同的一律不认）"
                     + (map.Count > 0 ? "\r\n" + detail.ToString().TrimEnd('\r', '\n') : ""));
        }

        /// <summary>这张图有没有「画面被确认是同一张」的散片。</summary>
        static string ContentOwner(string libRoot, WallpaperItem it, string imagePath)
        {
            // zz- 是本项目所有自检假条目的前缀，别让它们吃真实库里的散片。
            if (it.Id.StartsWith("zz-", StringComparison.OrdinalIgnoreCase)) return null;
            try { EnsureContentMap(libRoot); }
            catch (Exception ex)
            {
                Log.Note("!! 飘动视频内容比对失败：" + ex.Message);
                return null;
            }
            List<string> keys = KeysOf(it, imagePath);
            lock (Gate)
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    string v;
                    if (ContentMap.TryGetValue(keys[i], out v)) return v;
                }
            }
            return null;
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
            best = FromJobs(libRoot, it, imagePath);           // 工单里写死的 out
            if (best != null) return best;

            // 第 7 层：名字完全对不上的散片，只有画面确实和这张图是同一张才认。
            // 不再按下单时间「就近认领」—— 那样会把别的壁纸的片子播到新下载的图上。
            best = ContentOwner(libRoot, it, imagePath);
            if (best != null) { inferred = true; return best; }
            return null;
        }

        /// <summary>
        /// 只读判定「这张图标着动态时，到底有没有片子可播」——载入索引用。
        ///
        /// 11-15：以前 LibraryStore.Load 只认 videos\&lt;id&gt;.mp4，跟运行时这套
        /// 七层发现不一致。靠第 7 层画面判定认领散片的图（biliart-17714251-4）
        /// 一载入就被清成 Motion=false，点卡片只设静态壁纸、动态视频不自动播，
        /// 可角标用 VideoFor 又查得到片，于是显示「有飘动」却永远不播。
        /// 这里复用同一套 FindSource 规则，但**只找不搬**：绝不 File.Copy、
        /// 绝不写壁纸库，索引载入这件事保持纯读。
        /// </summary>
        public static bool CanPlay(string libRoot, WallpaperItem it, string imagePath)
        {
            if (it == null || it.IsVideo || string.IsNullOrEmpty(it.Id)
                || string.IsNullOrEmpty(libRoot)) return false;
            try
            {
                if (File.Exists(CloudGen.VideoPath(libRoot, it.Id))) return true;
                bool inferred;
                return FindSource(libRoot, it, imagePath, out inferred) != null;
            }
            catch
            {
                // 载入阶段宁可保留主人写的标记，也别因为一次扫描异常就误清成静态：
                // 运行时 ApplyItem 还会用 VideoFor 再判一次，真没片不会播。
                return true;
            }
        }

        /// <summary>
        /// 返回可播的规范路径 videos\&lt;id&gt;.mp4；没有任何相关视频时返回 null。
        /// 找到的文件不在规范位置就复制一份过去（原文件保留），并写日志说明来源。
        /// 内容判定命中的散片例外：直接播原文件，不复制、不改名、不往壁纸库写文件。
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

            // 内容比对确认同一画面的散片：直接播原文件，不动壁纸库。
            if (inferred)
            {
                bool fresh;
                lock (Gate)
                {
                    string tag = it.Id + "|" + src;
                    fresh = !ContentLogged.Contains(tag);
                    if (fresh) ContentLogged.Add(tag);
                }
                // 界面刷新每秒会问好几次，日志只在第一次认领时写一行。
                if (fresh)
                    Log.Note("飘动视频：内容比对确认同一画面 " + it.Id + " ← " + src
                             + "（视频首帧与源图指纹匹配；直接播原文件，不复制、不改名）");
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
