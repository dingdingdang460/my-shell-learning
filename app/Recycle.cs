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
    }
}
