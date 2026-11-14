using System;
using System.Collections.Generic;
using System.Text;

namespace WallpaperManager
{
    /// <summary>
    /// 拼音首字母，零依赖实现。
    ///
    /// 原理：GB2312 的**一级汉字（区 16-55）本身就是按拼音顺序排列的**，
    /// 所以只要把汉字的 GBK 编码拿去和 23 个声母的起始编码比大小，
    /// 就能得到它的首字母，不需要任何词库或外部 dll。
    ///
    /// 边界：二级汉字（区 56 之后，如「淼」「喆」）和生僻字返回空，不硬猜 ——
    /// 猜错比不支持更烦人（输 cs 出来一堆不相干的图）。
    /// </summary>
    public static class Pinyin
    {
        /// <summary>23 个声母在一级汉字里的起始编码（GBK 双字节），末尾补一个结束边界</summary>
        static readonly int[] Bound =
        {
            0xB0A1, 0xB0C5, 0xB2C1, 0xB4EE, 0xB6EA, 0xB7A2, 0xB8C1, 0xB9FE,
            0xBBF7, 0xBFA6, 0xC0AC, 0xC2E8, 0xC4C3, 0xC5B6, 0xC5BE, 0xC6DA,
            0xC8BB, 0xC8F6, 0xCBFA, 0xCDDA, 0xCEF4, 0xD1B9, 0xD4D1, 0xD7FA
        };

        /// <summary>与 Bound 一一对应（没有 I / U / V）</summary>
        const string Letters = "ABCDEFGHJKLMNOPQRSTWXYZ";

        const int FirstHan = 0xB0A1;
        const int LastHan = 0xD7F9;

        static readonly Encoding Gbk = CreateGbk();

        static Encoding CreateGbk()
        {
            try { return Encoding.GetEncoding(936); }
            catch { return null; }
        }

        // 词条 → 首字母串。库里标题就那几十条，算一次存着，输入时零成本。
        static readonly Dictionary<string, string> Cache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>取整串的首字母大写形式：「城市夜景」→「CSYJ」</summary>
        public static string Initials(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            string hit;
            if (Cache.TryGetValue(text, out hit)) return hit;

            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= 'a' && c <= 'z') { sb.Append((char)(c - 32)); continue; }
                if (c >= 'A' && c <= 'Z') { sb.Append(c); continue; }
                if (c >= '0' && c <= '9') { sb.Append(c); continue; }
                string one = FirstLetter(c);
                if (one.Length > 0) sb.Append(one);
            }

            string res = sb.ToString();
            if (Cache.Count > 4000) Cache.Clear();      // 别为了搜索把内存吃穿
            Cache[text] = res;
            return res;
        }

        static string FirstLetter(char c)
        {
            if (c < 0x4E00 || c > 0x9FA5) return "";
            if (Gbk == null) return "";

            byte[] b;
            try { b = Gbk.GetBytes(new char[] { c }); }
            catch { return ""; }
            if (b.Length < 2) return "";

            int code = (b[0] << 8) | b[1];
            if (code < FirstHan || code > LastHan) return "";      // 二级汉字 / 符号

            for (int i = Bound.Length - 2; i >= 0; i--)
            {
                if (code >= Bound[i]) return Letters.Substring(i, 1);
            }
            return "";
        }

        /// <summary>
        /// 关键词是否命中：中文按子串，字母按「原文」和「首字母串」两条都试。
        /// 所以 city / 城市 / cs 都能命中「城市夜景」。
        ///
        /// 注意 extra（分类搜索键，形如 "city 城市"）也要算首字母 —— 库里标题是英文的，
        /// 主人敲 cs 想找的是**分类**「城市」，只算标题首字母的话一张都搜不到。
        /// 这个 bug 是 11-13 用 --uitest q:cs 截图时发现的，别改回去。
        /// </summary>
        public static bool Matches(string title, string extra, string query)
        {
            if (string.IsNullOrEmpty(query)) return true;

            string q = query.Trim();
            if (q.Length == 0) return true;

            string t = title == null ? "" : title;
            if (t.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            if (!string.IsNullOrEmpty(extra)
                && extra.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // 只有纯字母才走首字母匹配，否则「城市」这种词没必要再算一遍
            for (int i = 0; i < q.Length; i++)
            {
                char c = q[i];
                bool letter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                if (!letter) return false;
            }

            if (Initials(t).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrEmpty(extra)
                && Initials(extra).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }
    }
}
