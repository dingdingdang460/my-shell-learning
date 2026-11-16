using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WallpaperManager
{
    /// <summary>一次「送回收站」的真实结局。11-16 加：以前只有 true/false，分不清「进了回收站」和「被永久删掉」。</summary>
    public enum RecycleOutcome
    {
        /// <summary>本来就没有这个文件</summary>
        NotFound = 0,
        /// <summary>确认进了回收站，能在回收站里还原</summary>
        Recycled = 1,
        /// <summary>文件消失了，但回收站里查不到 —— Windows 直接永久删了（网络盘 / 回收站被关 / 超出配额）</summary>
        Vanished = 2,
        /// <summary>压根没删掉（被占用、权限不足）</summary>
        Failed = 3,
        /// <summary>删掉了，但读不到回收站目录，无法确认去向</summary>
        Unverified = 4
    }

    /// <summary>
    /// 删除进回收站（11-16 新增，11-16 晚补验收）。
    ///
    /// 以前删壁纸是直接 File.Delete，误删一张十几 MB 的动态壁纸就真没了。
    /// 这里走 shell32 的 SHFileOperation + FOF_ALLOWUNDO，行为等同在资源管理器里
    /// 按 Delete：进回收站、可以还原。
    ///
    /// **关键改动**：SHFileOperation 返回 0 不代表「进了回收站」。Windows 在
    /// 回收站被关掉（NukeOnDelete）、文件超过该卷回收站配额、网络盘 / 可移动盘上
    /// 会**直接永久删除**，返回值照样是 0、文件照样不见了。只验「路径消失」就会把
    /// 「永久删掉」误报成「已进回收站」—— 主人 11-16 就是这么被误导的。
    /// 现在删完去该卷 $Recycle.Bin 数一遍 $I 记录，多出来一条才算真回收。
    /// </summary>
    public static class Recycle
    {
        /// <summary>
        /// 自检进程专用：true = 一律硬删，绝不往真实回收站里塞临时文件。
        ///
        /// 11-16 一天里自检往 D:\$Recycle.Bin 积了 484 条 $I 记录（每次 --guardtest 约 8 条），
        /// 主人得手工清理，而且 `--watermarktest` 那次光临时视频就 62MB 躺在回收站里。
        /// 自检造的东西没有任何还原价值，直接删最干净。
        ///
        /// guardtest 要**验证**「真的进了回收站」时会临时把它关掉，
        /// 断言跑完再用 PurgeRecordsFor 把自己造的那几条记录清掉 —— 功能验到了，垃圾不留。
        /// </summary>
        public static bool TestNoRecycle;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x0004;
        const ushort FOF_NOCONFIRMATION = 0x0010;
        const ushort FOF_ALLOWUNDO = 0x0040;
        const ushort FOF_NOERRORUI = 0x0400;

        /// <summary>
        /// 把文件或目录移进回收站，并回报真实结局。
        /// 不抛异常；调用方按 Outcome 决定是不管、报错还是留副本。
        /// </summary>
        public static RecycleOutcome MoveToRecycleBin(string path)
        {
            if (string.IsNullOrEmpty(path)) return RecycleOutcome.NotFound;
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return RecycleOutcome.NotFound;

                // 自检进程走这条：直接删，不产生回收站记录（见 TestNoRecycle 注释）
                if (TestNoRecycle)
                {
                    try
                    {
                        if (Directory.Exists(path)) Directory.Delete(path, true);
                        else File.Delete(path);
                    }
                    catch { return RecycleOutcome.Failed; }
                    bool alive = File.Exists(path) || Directory.Exists(path);
                    return alive ? RecycleOutcome.Failed : RecycleOutcome.Vanished;
                }

                int before = CountRecords(path);

                SHFILEOPSTRUCT op = new SHFILEOPSTRUCT();
                op.wFunc = FO_DELETE;
                // pFrom 是「双 null 结尾」的路径列表；只放一个路径也要两个 \0
                op.pFrom = path + "\0\0";
                op.fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI);

                int rc = SHFileOperation(ref op);

                bool gone = !File.Exists(path) && !Directory.Exists(path);
                if (!gone)
                {
                    // 东西还在原地：被占用 / 没权限 / 用户在别处取消
                    return RecycleOutcome.Failed;
                }
                if (rc != 0 || op.fAnyOperationsAborted)
                {
                    // 明明报了错，文件却不见了 —— 只能按「已永久删除」处理，让上层留副本
                    return RecycleOutcome.Vanished;
                }

                int after = CountRecords(path);
                if (before < 0 || after < 0) return RecycleOutcome.Unverified;
                return after > before ? RecycleOutcome.Recycled : RecycleOutcome.Vanished;
            }
            catch { return RecycleOutcome.Failed; }
        }

        /// <summary>
        /// 数该卷回收站里的 $I 记录条数（每个被删项一条）。
        /// 读不到（权限 / 卷没有回收站）返回 -1，调用方据此判 Unverified。
        /// 只看条数变化，不解析内容，够用且不依赖 SID。
        /// </summary>
        public static int CountRecords(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return -1;

                string bin = Path.Combine(root, "$Recycle.Bin");
                if (!Directory.Exists(bin)) return -1;

                int n = 0;
                int readable = 0;
                string[] subs = Directory.GetDirectories(bin);
                for (int i = 0; i < subs.Length; i++)
                {
                    try
                    {
                        string[] files = Directory.GetFiles(subs[i], "$I*");
                        n += files.Length;
                        readable++;
                    }
                    catch { }   // 别的用户名的子目录没权限，跳过；一条都读不到才算无法确认
                }
                return readable == 0 ? -1 : n;
            }
            catch { return -1; }
        }

        /// <summary>
        /// 把某条原始路径对应的回收站记录彻底清掉（$R 实体 + $I 记录），返回清掉几条。
        /// 只给自检收尾用：guardtest 为了让断言验到「真进了回收站」必须真删一次，
        /// 但那些用完全没用的临时文件不该留在主人的回收站里。
        /// 匹配方式是解析 $I 里记的原路径，精确到全路径、忽略大小写，绝不按时间猜测。
        /// </summary>
        public static int PurgeRecordsFor(string originalPath)
        {
            if (string.IsNullOrEmpty(originalPath)) return 0;
            int purged = 0;
            try
            {
                string full = Path.GetFullPath(originalPath);
                string root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return 0;

                string bin = Path.Combine(root, "$Recycle.Bin");
                if (!Directory.Exists(bin)) return 0;

                string[] subs = Directory.GetDirectories(bin);
                for (int s = 0; s < subs.Length; s++)
                {
                    string[] idx;
                    try { idx = Directory.GetFiles(subs[s], "$I*"); }
                    catch { continue; }     // 别的用户的子目录，没权限就算了

                    for (int i = 0; i < idx.Length; i++)
                    {
                        if (!RecordMatches(idx[i], full)) continue;

                        string name = Path.GetFileName(idx[i]);
                        string mate = Path.Combine(Path.GetDirectoryName(idx[i]), "$R" + name.Substring(2));
                        try
                        {
                            if (Directory.Exists(mate)) Directory.Delete(mate, true);
                            else if (File.Exists(mate)) File.Delete(mate);
                            File.Delete(idx[i]);
                            purged++;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return purged;
        }

        /// <summary>$I 文件里记的原始路径是否就是目标（$I v2 有名字长度字段，$I v1 直接从 24 起）</summary>
        static bool RecordMatches(string iFile, string fullPath)
        {
            try
            {
                byte[] b = File.ReadAllBytes(iFile);
                if (b.Length < 24) return false;

                long ver = BitConverter.ToInt64(b, 0);
                string path;
                if (ver == 2)
                {
                    if (b.Length < 28) return false;
                    int len = BitConverter.ToInt32(b, 24);
                    if (len <= 0 || 28 + len * 2 > b.Length) return false;
                    path = System.Text.Encoding.Unicode.GetString(b, 28, len * 2);
                }
                else
                {
                    path = System.Text.Encoding.Unicode.GetString(b, 24, b.Length - 24);
                }
                path = path.TrimEnd('\0');
                return string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
