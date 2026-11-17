using System;
using System.Collections.Generic;
using System.Globalization;

namespace WallpaperManager
{
    /// <summary>
    /// 11-17 时段轮换：把一天切成若干命名时段，按当前时刻挑对应组的壁纸轮换。
    /// 时段表可编辑（AppConfig.TimeSlots），规则不写死在代码里 —— 主人想怎么分就怎么分。
    ///
    /// 时段表格式：组名|HH:MM-HH:MM;组名|HH:MM-HH:MM;...
    ///   例：早晨|06:00-09:00;白天|09:00-18:00;夜晚|18:00-06:00
    ///   跨零点用「起 &gt; 止」表示（上面的夜晚 = 18:00 到次日 06:00）。
    ///   区间取左闭右开 [起, 止)：06:00 整点归早晨、不归夜晚，边界不重叠也不留缝。
    ///
    /// 壁纸用 WallpaperItem.Slot 标的组名归属；空串 = 不分组，任何时段都参与。
    /// 这里全是纯函数（不碰文件、不碰系统时钟以外的状态），便于自检逐条覆盖。
    /// </summary>
    public static class TimeSlots
    {
        public class Range
        {
            public string Name = "";
            public int Start;   // 当天分钟数 0-1439
            public int End;     // 当天分钟数 0-1439
            public bool Wrap;   // 起 > 止 = 跨零点
        }

        /// <summary>解析时段表。格式错的段直接跳过，不抛异常、不让整张表失效。</summary>
        public static List<Range> Parse(string spec)
        {
            List<Range> list = new List<Range>();
            if (string.IsNullOrEmpty(spec)) return list;
            string[] segs = spec.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segs.Length; i++)
            {
                string seg = segs[i].Trim();
                if (seg.Length == 0) continue;
                int bar = seg.IndexOf('|');
                if (bar <= 0 || bar >= seg.Length - 1) continue;
                string name = seg.Substring(0, bar).Trim();
                string span = seg.Substring(bar + 1).Trim();
                if (name.Length == 0) continue;
                int dash = span.IndexOf('-');
                if (dash <= 0 || dash >= span.Length - 1) continue;
                int start = Minute(span.Substring(0, dash).Trim());
                int end = Minute(span.Substring(dash + 1).Trim());
                if (start < 0 || end < 0 || start == end) continue;
                Range r = new Range();
                r.Name = name;
                r.Start = start;
                r.End = end;
                r.Wrap = start > end;
                list.Add(r);
            }
            return list;
        }

        /// <summary>把 "HH:MM" 转成当天分钟数；非法返回 -1。</summary>
        public static int Minute(string hhmm)
        {
            if (string.IsNullOrEmpty(hhmm)) return -1;
            string[] p = hhmm.Split(new char[] { ':' });
            if (p.Length != 2) return -1;
            int h, m;
            if (!int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out h)) return -1;
            if (!int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out m)) return -1;
            if (h < 0 || h > 23 || m < 0 || m > 59) return -1;
            return h * 60 + m;
        }

        /// <summary>某个时刻（当天分钟数）是否落在这个时段内，含跨零点。</summary>
        public static bool Contains(Range r, int minuteOfDay)
        {
            if (r == null || minuteOfDay < 0 || minuteOfDay > 1439) return false;
            if (r.Wrap) return minuteOfDay >= r.Start || minuteOfDay < r.End;
            return minuteOfDay >= r.Start && minuteOfDay < r.End;
        }

        /// <summary>当前时刻命中哪个组；不在任何时段（表有缝或为空）返回空串。</summary>
        public static string CurrentGroup(List<Range> ranges, DateTime now)
        {
            if (ranges == null) return "";
            int m = now.Hour * 60 + now.Minute;
            for (int i = 0; i < ranges.Count; i++)
                if (Contains(ranges[i], m)) return ranges[i].Name;
            return "";
        }

        /// <summary>
        /// 按当前时段从全集里挑候选池：命中的组 + 所有不分组的图（永远参与）。
        /// 该组一张图都没有、或当前不在任何时段 → 退回全集，绝不让轮换卡死/空池。
        /// 返回新列表，不改动传入的 items。
        /// </summary>
        public static List<WallpaperItem> Candidates(List<WallpaperItem> items, string group)
        {
            List<WallpaperItem> result = new List<WallpaperItem>();
            if (items == null) return result;
            if (items.Count == 0) return result;

            bool anyGrouped = false;
            if (!string.IsNullOrEmpty(group))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    WallpaperItem it = items[i];
                    if (it != null && string.Equals(it.Slot, group, StringComparison.Ordinal))
                    { anyGrouped = true; break; }
                }
            }

            if (string.IsNullOrEmpty(group) || !anyGrouped)
            {
                // 不在任何时段 / 该组没图：全集照旧轮换，行为退回固定间隔那一套。
                for (int i = 0; i < items.Count; i++) if (items[i] != null) result.Add(items[i]);
                return result;
            }

            // 保持 items 里的原有先后顺序：先命中的组，再不分组的图。
            for (int i = 0; i < items.Count; i++)
            {
                WallpaperItem it = items[i];
                if (it != null && string.Equals(it.Slot, group, StringComparison.Ordinal)) result.Add(it);
            }
            for (int i = 0; i < items.Count; i++)
            {
                WallpaperItem it = items[i];
                if (it != null && string.IsNullOrEmpty(it.Slot)) result.Add(it);
            }
            return result;
        }
    }
}
