using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WallpaperManager
{
    /// <summary>
    /// 删除进回收站（11-16 新增）。
    ///
    /// 以前删壁纸是直接 File.Delete，误删一张十几 MB 的动态壁纸就真没了。
    /// 这里走 shell32 的 SHFileOperation + FOF_ALLOWUNDO，行为等同在资源管理器里
    /// 按 Delete：进回收站、可以还原。
    ///
    /// 回收站不可用（网络盘 / 权限 / 已被策略关闭）时返回 false，
    /// 由调用方决定退回硬删 —— 删除流程不能因为这个能力缺失就卡住。
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
        /// 把文件或目录移进回收站。
        /// 成功（操作返回 0、未被中止、路径确实已不存在）返回 true；
        /// 任何一步不成立都返回 false，不抛异常。
        /// </summary>
        public static bool MoveToRecycleBin(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return false;

                SHFILEOPSTRUCT op = new SHFILEOPSTRUCT();
                op.wFunc = FO_DELETE;
                // pFrom 是「双 null 结尾」的路径列表；只放一个路径也要两个 \0
                op.pFrom = path + "\0\0";
                op.fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI);

                int rc = SHFileOperation(ref op);
                if (rc != 0) return false;
                if (op.fAnyOperationsAborted) return false;

                return !File.Exists(path) && !Directory.Exists(path);
            }
            catch { return false; }
        }
    }
}
