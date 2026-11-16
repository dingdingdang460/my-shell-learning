using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace WallpaperManager
{
    /// <summary>本地壁纸库：读 index.json、扫描 images 目录、按需下载 4K 封面</summary>
    public static class LibraryStore
    {
        public static List<WallpaperItem> Load(string root)
        {
            List<WallpaperItem> list = new List<WallpaperItem>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return list;

            string indexFile = Path.Combine(root, "index.json");
            if (File.Exists(indexFile))
            {
                try
                {
                    object rootObj = Json.Parse(File.ReadAllText(indexFile, Encoding.UTF8));
                    object[] arr = Json.AsArr(rootObj);
                    if (arr != null)
                    {
                        foreach (object o in arr)
                        {
                            Dictionary<string, object> d = Json.AsDict(o);
                            if (d == null) continue;

                            WallpaperItem it = new WallpaperItem();
                            it.Id = Json.Str(d, "id");
                            it.Title = Json.Str(d, "title");
                            it.Tag = Json.Str(d, "tag");
                            it.File = Json.Str(d, "file");
                            it.Thumb = Json.Str(d, "thumb");
                            it.Bytes = Json.Long(d, "bytes");
                            it.Duration = Json.Dbl(d, "duration_s");
                            it.Upgrade4k = Json.Str(d, "upgrade_4k");
                            it.Motion = Json.Bool(d, "motion", false);
                            it.Fav = Json.Bool(d, "fav", false);
                            // 动效方式现在只有一档：2 = 云端飘动视频。
                            // 0（场景动效）和 1（缓慢缩放）两种本机算法都已经删掉，
                            // 老库里残留的这类条目读进来直接退回静态，免得界面说「动态」桌面却不动。
                            it.Effect = (int)Json.Long(d, "fx");
                            if (it.Effect != 2)
                            {
                                it.Effect = 0;
                                it.Motion = false;
                            }
                            // 「标着动态但根本没片子可播」的降级放到下面路径字段赋值之后做，
                            // 因为要跟运行时一样走 RelatedVideo 的七层发现（含按画面认散片）。
                            // 按文件后缀判断动态/静态：早先一律当视频，导致专栏图集被当成视频设不上壁纸
                            string lowFile = it.File.ToLowerInvariant();
                            it.IsVideo = lowFile.EndsWith(".mp4") || lowFile.EndsWith(".webm")
                                      || lowFile.EndsWith(".mov");
                            if (it.Id.Length == 0) continue;
                            if (it.Title.Length == 0) it.Title = it.Id;
                            it.ThumbAbs = ToAbs(root, it.Thumb);
                            it.ImagePath = it.IsVideo ? ExistingCover(root, it.Id) : ToAbs(root, it.File);
                            if (!it.IsVideo && (it.ImagePath == null || !File.Exists(it.ImagePath)))
                                it.ImagePath = ExistingCover(root, it.Id);
                            if (!it.IsVideo && it.ThumbAbs.Length == 0) it.ThumbAbs = it.ImagePath;

                            // 11-15：只有「真的一片都找不到」才降级为静态，否则菜单会写
                            // 「当前：云端飘动视频」而桌面不动。旧版只认 videos\<id>.mp4，
                            // 把靠画面判定认领散片的图（biliart-17714251-4）误清成静态，
                            // 导致点卡片切换壁纸时不自动播放已有动态视频。
                            // CanPlay 是纯只读判定：不复制、不改名、不写壁纸库。
                            if (it.Motion && !RelatedVideo.CanPlay(root, it, it.ImagePath))
                            {
                                it.Motion = false;
                                it.Effect = 0;
                            }
                            list.Add(it);
                        }
                    }
                }
                catch { }
            }

            // images 目录里不属于上面任何一条的图片，按静态壁纸收录
            string imgDir = Path.Combine(root, "images");
            if (Directory.Exists(imgDir))
            {
                string[] exts = new string[] { ".jpg", ".jpeg", ".png", ".bmp" };
                foreach (string f in Directory.GetFiles(imgDir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    bool okExt = false;
                    for (int i = 0; i < exts.Length; i++) if (exts[i] == ext) okExt = true;
                    if (!okExt) continue;

                    string id = Path.GetFileNameWithoutExtension(f);
                    bool dup = false;
                    for (int i = 0; i < list.Count; i++) if (list[i].Id == id) dup = true;
                    if (dup) continue;

                    WallpaperItem it = new WallpaperItem();
                    it.Id = id;
                    it.Title = id;
                    it.Tag = "static";
                    it.IsVideo = false;
                    it.ImagePath = f;
                    it.ThumbAbs = f;
                    it.Bytes = new FileInfo(f).Length;
                    list.Add(it);
                }
            }

            return list;
        }

        static string ToAbs(string root, string rel)
        {
            if (string.IsNullOrEmpty(rel)) return "";
            return Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        static string ExistingCover(string root, string id)
        {
            try
            {
                string p = Path.Combine(root, "images", id + ".jpg");
                if (File.Exists(p) && new FileInfo(p).Length > 20000) return p;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 从库里删掉一条：先重写 index.json（去掉这个 id），再删磁盘文件。
        ///
        /// 顺序很重要 —— 先改索引，即使后面删文件失败，条目也不会再次出现在列表里；
        /// 反过来先删文件、索引写失败的话，库里会留一条指向不存在文件的黑卡。
        /// 删文件前会校验路径确实在库根下面，避免 index.json 里被人写了
        /// 绝对路径或 ../ 时误删库外的文件。
        /// </summary>
        public static bool Delete(WallpaperItem item, string root, out string err)
        {
            err = "";
            if (item == null) { err = "条目为空"; return false; }
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { err = "壁纸库目录不存在"; return false; }

            string indexFile = Path.Combine(root, "index.json");

            // 1) 重写 index.json，去掉这一条
            if (File.Exists(indexFile))
            {
                List<object> keep = new List<object>();
                try
                {
                    object rootObj = Json.Parse(File.ReadAllText(indexFile, Encoding.UTF8));
                    object[] arr = Json.AsArr(rootObj);
                    if (arr != null)
                    {
                        foreach (object o in arr)
                        {
                            Dictionary<string, object> d = Json.AsDict(o);
                            if (d != null && Json.Str(d, "id") == item.Id) continue;
                            keep.Add(o);
                        }
                    }
                }
                catch (Exception ex)
                {
                    err = "读取索引失败：" + ex.Message;
                    return false;
                }

                try
                {
                    File.Copy(indexFile, indexFile + ".bak", true);   // 留一份可回滚
                    File.WriteAllText(indexFile, Json.Serialize(keep), new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    err = "写索引失败：" + ex.Message;
                    return false;
                }
            }

            // 2) 删文件：主文件、缩略图、封面
            DeleteUnder(root, ToAbs(root, item.File));
            DeleteUnder(root, ToAbs(root, item.Thumb));
            DeleteUnder(root, Path.Combine(root, "images", item.Id + ".jpg"));
            DeleteUnder(root, Path.Combine(root, "thumbs", item.Id + ".jpg"));
            DeleteUnder(root, Path.Combine(root, "images", item.Id + ".png"));
            return true;
        }

        /// <summary>
        /// 给某一条记上「是否以动态方式播放」+「用哪种动效」。静态图本来没有视频轨道，
        /// 这里只是把用户的选择写进 index.json，下次开程序还按这个来。
        /// 散图（images 里不属于任何条目、也没在 index.json 里的图）第一次设置时会补一条最小记录。
        /// </summary>
        public static bool SetMotion(string id, string root, bool motion, int effect, out string err)
        {
            err = "";
            if (effect < 0 || effect > 2) effect = 0;
            if (string.IsNullOrEmpty(id)) { err = "条目 id 为空"; return false; }
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { err = "壁纸库目录不存在"; return false; }

            string indexFile = Path.Combine(root, "index.json");
            List<object> all = new List<object>();
            bool found = false;

            try
            {
                if (File.Exists(indexFile))
                {
                    object rootObj = Json.Parse(File.ReadAllText(indexFile, Encoding.UTF8));
                    object[] arr = Json.AsArr(rootObj);
                    if (arr != null)
                    {
                        foreach (object o in arr)
                        {
                            Dictionary<string, object> d = Json.AsDict(o);
                            if (d != null && Json.Str(d, "id") == id)
                            {
                                d["motion"] = motion;
                                d["fx"] = effect;
                                found = true;
                            }
                            all.Add(o);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                err = "读取索引失败：" + ex.Message;
                return false;
            }

            if (!found)
            {
                // 散图：补一条最小记录，否则这个开关下次启动就丢了
                string file = "";
                string[] exts = new string[] { ".jpg", ".jpeg", ".png", ".bmp" };
                for (int i = 0; i < exts.Length; i++)
                {
                    string p = Path.Combine(root, "images", id + exts[i]);
                    if (File.Exists(p)) { file = "images/" + id + exts[i]; break; }
                }
                if (file.Length == 0) { err = "库里找不到这张图：" + id; return false; }

                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = id;
                d["title"] = id;
                d["tag"] = "static";
                d["file"] = file;
                d["motion"] = motion;
                d["fx"] = effect;
                all.Add(d);
            }

            try
            {
                if (File.Exists(indexFile)) File.Copy(indexFile, indexFile + ".bak", true);
                File.WriteAllText(indexFile, Json.Serialize(all), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                err = "写索引失败：" + ex.Message;
                return false;
            }
            return true;
        }

        /// <summary>只删库根目录之下的文件；越界一律不动（防误删）</summary>
        static void DeleteUnder(string root, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string full = Path.GetFullPath(path);
                string guard = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
                if (!full.StartsWith(guard, StringComparison.OrdinalIgnoreCase)) return;
                if (!File.Exists(full)) return;

                // 11-16：先进回收站，误删还能捞回来；回收站不可用（网络盘 / 策略关闭）
                // 才退回硬删，保持「删除就一定删掉」的旧语义，不让能力缺失卡住删除流程。
                if (Recycle.MoveToRecycleBin(full)) return;
                File.Delete(full);
            }
            catch { }
        }

        /// <summary>
        /// 收藏 / 取消收藏。和 SetMotion 一样只改 index.json 里一个字段。
        /// 散图（images 里没有索引记录的图）第一次收藏时补一条最小记录，
        /// 否则下次启动这个星标就丢了。
        /// </summary>
        public static bool SetFav(string id, string root, bool fav, out string err)
        {
            err = "";
            if (string.IsNullOrEmpty(id)) { err = "条目 id 为空"; return false; }
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { err = "壁纸库目录不存在"; return false; }

            string indexFile = Path.Combine(root, "index.json");
            List<object> all = new List<object>();
            bool found = false;

            try
            {
                if (File.Exists(indexFile))
                {
                    object[] arr = Json.AsArr(Json.Parse(File.ReadAllText(indexFile, Encoding.UTF8)));
                    if (arr != null)
                        foreach (object o in arr)
                        {
                            Dictionary<string, object> d = Json.AsDict(o);
                            if (d != null && Json.Str(d, "id") == id) { d["fav"] = fav; found = true; }
                            all.Add(o);
                        }
                }
            }
            catch (Exception ex) { err = "读取索引失败：" + ex.Message; return false; }

            if (!found)
            {
                string file = "";
                string[] exts = new string[] { ".jpg", ".jpeg", ".png", ".bmp" };
                for (int i = 0; i < exts.Length; i++)
                {
                    string p = Path.Combine(root, "images", id + exts[i]);
                    if (File.Exists(p)) { file = "images/" + id + exts[i]; break; }
                }
                if (file.Length == 0) { err = "库里找不到这张图：" + id; return false; }

                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = id;
                d["title"] = id;
                d["tag"] = "static";
                d["file"] = file;
                d["fav"] = fav;
                all.Add(d);
            }

            try
            {
                if (File.Exists(indexFile)) File.Copy(indexFile, indexFile + ".bak", true);
                File.WriteAllText(indexFile, Json.Serialize(all), new UTF8Encoding(false));
            }
            catch (Exception ex) { err = "写索引失败：" + ex.Message; return false; }
            return true;
        }

        /// <summary>
        /// 把一个本地文件收进库（拖拽 / 批量导入都走这里）。
        ///
        /// 图片只要复制到 images\ 就会被 Load 里的目录扫描自动收录，不用改索引；
        /// 视频必须补一条索引记录（videos 目录不扫描，怕把以前的手动文件也吸进来）。
        /// 同名不覆盖：自动加 -2 / -3 后缀，主人的原文件永远不动。
        /// 成功返回新条目的 id，失败返回 null。
        /// </summary>
        public static string ImportFile(string src, string root, out string err)
        {
            err = "";
            if (string.IsNullOrEmpty(src) || !File.Exists(src)) { err = "文件不存在"; return null; }

            try
            {
                string ext = Path.GetExtension(src).ToLowerInvariant();
                bool isImg = ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp";
                bool isVid = ext == ".mp4" || ext == ".mov" || ext == ".webm";
                if (!isImg && !isVid) { err = "不支持的类型：" + ext; return null; }

                string sub = isImg ? "images" : "videos";
                string dir = Path.Combine(root, sub);
                Directory.CreateDirectory(dir);

                string baseName = Path.GetFileNameWithoutExtension(src);
                foreach (char c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
                if (baseName.Length > 48) baseName = baseName.Substring(0, 48);
                if (baseName.Length == 0) baseName = "import";

                string id = baseName;
                string dst = Path.Combine(dir, id + ext);
                int n = 2;
                while (File.Exists(dst) || IdExists(root, id))
                {
                    id = baseName + "-" + n;
                    dst = Path.Combine(dir, id + ext);
                    n++;
                    if (n > 999) { err = "同名的文件太多了"; return null; }
                }

                if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
                {
                    err = "文件已经在库里了";
                    return null;
                }

                File.Copy(src, dst, false);

                if (isVid)
                {
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["id"] = id;
                    d["title"] = baseName;
                    d["tag"] = "local";
                    d["file"] = "videos/" + id + ext;
                    d["bytes"] = new FileInfo(dst).Length;
                    AppendIndex(root, d);
                }
                return id;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return null;
            }
        }

        static bool IdExists(string root, string id)
        {
            try
            {
                string indexFile = Path.Combine(root, "index.json");
                if (!File.Exists(indexFile)) return false;
                object[] arr = Json.AsArr(Json.Parse(File.ReadAllText(indexFile, Encoding.UTF8)));
                if (arr == null) return false;
                foreach (object o in arr)
                {
                    Dictionary<string, object> d = Json.AsDict(o);
                    if (d != null && Json.Str(d, "id") == id) return true;
                }
            }
            catch { }
            return false;
        }

        static void AppendIndex(string root, Dictionary<string, object> rec)
        {
            string indexFile = Path.Combine(root, "index.json");
            List<object> all = new List<object>();
            try
            {
                if (File.Exists(indexFile))
                {
                    object[] arr = Json.AsArr(Json.Parse(File.ReadAllText(indexFile, Encoding.UTF8)));
                    if (arr != null) foreach (object o in arr) all.Add(o);
                    File.Copy(indexFile, indexFile + ".bak", true);
                }
                all.Add(rec);
                File.WriteAllText(indexFile, Json.Serialize(all), new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>确保 4K 封面已在本地；返回绝对路径，失败返回 null</summary>
        public static string EnsureCover(WallpaperItem item, string root)
        {
            string existing = ExistingCover(root, item.Id);
            if (existing != null) return existing;
            if (item.IsVideo == false && !string.IsNullOrEmpty(item.ImagePath)) return item.ImagePath;
            if (string.IsNullOrEmpty(item.Upgrade4k)) return null;

            string url = item.Upgrade4k.Replace(".mp4", ".jpg");
            string dir = Path.Combine(root, "images");
            string target = Path.Combine(dir, item.Id + ".jpg");
            string tmp = target + ".part";

            try
            {
                Directory.CreateDirectory(dir);
                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                    Net.Apply(wc);
                    wc.DownloadFile(url, tmp);
                }
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                if (new FileInfo(target).Length < 20000)
                {
                    File.Delete(target);
                    return null;
                }
                return target;
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return null;
            }
        }
    }

    /// <summary>配置读写 %APPDATA%\wallpaper\config.json</summary>
    public static class ConfigStore
    {
        /// <summary>
        /// 自检隔离：设成临时目录后，config.json 和 jobs\ 全部落到那里，
        /// --cloudflowtest 用的假 Key / 假工单不会碰到主人的真实配置与队列。
        /// 正常启动永远是空，行为与以前完全一致。
        /// </summary>
        public static string DirOverride = "";

        public static string Dir
        {
            get
            {
                if (!string.IsNullOrEmpty(DirOverride)) return DirOverride;
                string custom = CustomRoot();
                if (custom.Length > 0) return custom;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "wallpaper");
            }
        }

        /// <summary>
        /// 数据目录可以搬出 C 盘：在程序所在目录放一个 data-root.txt，
        /// 里面写一行目标目录（例如 D:\...\wallpaper\appdata），配置、工单、欢迎语就都落到那里。
        ///
        /// 没有这个文件时行为与以前一模一样（%APPDATA%\wallpaper），老配置和分发包完全无感。
        /// 目标目录必须真实存在：写错、盘没插、目录被删，一律退回默认位置，
        /// 不会让程序在某个不存在的地方重新生成一份空配置、把主人的设置丢掉。
        /// </summary>
        static string CustomRoot()
        {
            try
            {
                string marker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data-root.txt");
                if (!File.Exists(marker)) return "";
                string s = File.ReadAllText(marker).Trim().Trim('"');
                if (s.Length == 0) return "";
                if (!Directory.Exists(s)) return "";
                return s;
            }
            catch { return ""; }
        }

        /// <summary>数据目录当前是不是被搬过（设置页与日志用来说明落点）。</summary>
        public static bool Relocated { get { return CustomRoot().Length > 0; } }

        public static string FilePath
        {
            get { return Path.Combine(Dir, "config.json"); }
        }

        public static AppConfig Load()
        {
            return LoadFrom(FilePath);
        }

        /// <summary>从指定文件读配置。抽出来是为了「导入配置」和自检能验证同一条解析路径。</summary>
        public static AppConfig LoadFrom(string path)
        {
            AppConfig c = new AppConfig();
            try
            {
                if (!File.Exists(path)) return c;
                Dictionary<string, object> d = Json.AsDict(Json.Parse(File.ReadAllText(path, Encoding.UTF8)));
                if (d == null) return c;

                string r = Json.Str(d, "libRoot");
                if (r.Length > 0) c.LibRoot = r;

                long iv = Json.Long(d, "intervalMinutes");
                if (iv >= 5) c.IntervalMinutes = (int)iv;

                c.AutoRotate = Json.Bool(d, "autoRotate", true);
                c.LastId = Json.Str(d, "lastId");

                c.AutoStart = Json.Bool(d, "autoStart", false);
                c.WelcomeOnBoot = Json.Bool(d, "welcomeOnBoot", true);
                c.WelcomeOnUnlock = Json.Bool(d, "welcomeOnUnlock", true);
                c.FilterPortrait = Json.Bool(d, "filterPortrait", false);   // 默认不过滤：竖图现在会自动适配
                long fps = Json.Long(d, "dynamicFps");
                if (fps >= 15 && fps <= 60) c.DynamicFps = (int)fps;
                c.LastDynamicId = Json.Str(d, "lastDynamicId");
                c.DynamicEnabled = Json.Bool(d, "dynamicEnabled", false);
                long mm = Json.Long(d, "mountMode");
                if (mm >= 0 && mm <= 8) c.MountMode = (int)mm;

                long gs = Json.Long(d, "genSeconds");
                if (gs >= 1 && gs <= CloudGen.MaxSeconds) c.GenSeconds = (int)gs;
                c.PendingCloudId = Json.Str(d, "pendingCloudId");

                // 云端直连（GenApi.cs）：provider 为空或未知就当「不用 API」，退回到本地导入/工单
                c.GenProvider = Json.Str(d, "genProvider");
                c.GenApiKey = Json.Str(d, "genApiKey");
                c.GenModel = Json.Str(d, "genModel");
                c.GenResolution = Json.Str(d, "genResolution");
                if (string.IsNullOrEmpty(c.GenResolution)) c.GenResolution = "720P";
                // 11-15：老配置没有 genChannel → 默认「优先 WorkBuddy 积分」
                string chan = Json.Str(d, "genChannel");
                c.GenChannel = (chan == "api" || chan == "buddy") ? chan : "buddy";
                c.BuddyExe = Json.Str(d, "buddyExe");

                c.Proxy = Json.Str(d, "proxy");
                long cmb = Json.Long(d, "cacheLimitMb");
                if (cmb >= 50 && cmb <= 20000) c.CacheLimitMb = (int)cmb;

                // 11-16 功耗守卫：以前这里只有一行写着「功耗守卫」的注释，实际没有任何检测代码。
                // 现在真的读这两个键：老配置缺键 → 守卫默认开、阈值 20%。
                // Json.Long 缺键返回 0，所以只用「>0 且在区间内」覆盖默认值；
                // 想彻底关掉守卫用 pauseOnBattery=false，不需要把阈值填 0。
                c.PauseOnBattery = Json.Bool(d, "pauseOnBattery", true);
                long bpp = Json.Long(d, "batteryPausePercent");
                if (bpp > 0 && bpp <= 100) c.BatteryPausePercent = (int)bpp;

                // 11-16 角标验收：老配置缺键 → 自动修补默认开（主人拍板的硬规则是
                // 「有角标必须修干净才交付」，默认开才对；不想让程序动片子可以在设置里关）。
                c.FfmpegPath = Json.Str(d, "ffmpegPath");
                c.AutoDelogo = Json.Bool(d, "autoDelogo", true);
            }
            catch { }
            return c;
        }

        public static void Save(AppConfig c)
        {
            WriteTo(c, FilePath);
        }

        /// <summary>把配置写到指定文件（导出 / 自检用；正常保存走 Save）</summary>
        public static bool WriteTo(AppConfig c, string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                StringBuilder sb = new StringBuilder();
                sb.Append("{\r\n");
                sb.Append("  \"libRoot\": ").Append(Json.Quote(c.LibRoot)).Append(",\r\n");
                sb.Append("  \"intervalMinutes\": ").Append(c.IntervalMinutes).Append(",\r\n");
                sb.Append("  \"autoRotate\": ").Append(c.AutoRotate ? "true" : "false").Append(",\r\n");
                sb.Append("  \"lastId\": ").Append(Json.Quote(c.LastId)).Append(",\r\n");
                sb.Append("  \"autoStart\": ").Append(c.AutoStart ? "true" : "false").Append(",\r\n");
                sb.Append("  \"welcomeOnBoot\": ").Append(c.WelcomeOnBoot ? "true" : "false").Append(",\r\n");
                sb.Append("  \"welcomeOnUnlock\": ").Append(c.WelcomeOnUnlock ? "true" : "false").Append(",\r\n");
                sb.Append("  \"filterPortrait\": ").Append(c.FilterPortrait ? "true" : "false").Append(",\r\n");
                sb.Append("  \"dynamicFps\": ").Append(c.DynamicFps).Append(",\r\n");
                sb.Append("  \"lastDynamicId\": ").Append(Json.Quote(c.LastDynamicId)).Append(",\r\n");
                sb.Append("  \"dynamicEnabled\": ").Append(c.DynamicEnabled ? "true" : "false").Append(",\r\n");
                sb.Append("  \"mountMode\": ").Append(c.MountMode).Append(",\r\n");
                sb.Append("  \"genSeconds\": ").Append(c.GenSeconds).Append(",\r\n");
                sb.Append("  \"pendingCloudId\": ").Append(Json.Quote(c.PendingCloudId)).Append(",\r\n");
                sb.Append("  \"genProvider\": ").Append(Json.Quote(c.GenProvider)).Append(",\r\n");
                sb.Append("  \"genApiKey\": ").Append(Json.Quote(c.GenApiKey)).Append(",\r\n");
                sb.Append("  \"genModel\": ").Append(Json.Quote(c.GenModel)).Append(",\r\n");
                sb.Append("  \"genResolution\": ").Append(Json.Quote(c.GenResolution)).Append(",\r\n");
                sb.Append("  \"genChannel\": ").Append(Json.Quote(c.GenChannel)).Append(",\r\n");
                sb.Append("  \"buddyExe\": ").Append(Json.Quote(c.BuddyExe)).Append(",\r\n");
                sb.Append("  \"proxy\": ").Append(Json.Quote(c.Proxy)).Append(",\r\n");
                sb.Append("  \"cacheLimitMb\": ").Append(c.CacheLimitMb).Append(",\r\n");
                sb.Append("  \"pauseOnBattery\": ").Append(c.PauseOnBattery ? "true" : "false").Append(",\r\n");
                sb.Append("  \"batteryPausePercent\": ").Append(c.BatteryPausePercent).Append(",\r\n");
                sb.Append("  \"ffmpegPath\": ").Append(Json.Quote(c.FfmpegPath)).Append(",\r\n");
                sb.Append("  \"autoDelogo\": ").Append(c.AutoDelogo ? "true" : "false").Append("\r\n");
                sb.Append("}\r\n");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }
    }
}
