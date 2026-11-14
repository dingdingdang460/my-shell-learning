using System;
using System.Collections.Generic;

namespace WallpaperManager
{
    /// <summary>
    /// 分类的中英对照。图源给的是英文 tag，界面是中文，用户自然输中文，
    /// 所以搜索与显示都走这张表；未收录的 tag 原样返回，不影响新分类接入。
    /// </summary>
    static class TagNames
    {
        static readonly Dictionary<string, string> Map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static TagNames()
        {
            Map["city"] = "城市";
            Map["ocean"] = "海洋";
            Map["abstract"] = "抽象";
            Map["nature"] = "自然";
            Map["space"] = "太空";
            Map["fire"] = "火焰";
            Map["car"] = "汽车";
            Map["rain"] = "雨夜";
            Map["anime"] = "动漫";
            Map["game"] = "游戏";
            Map["bili"] = "B站";
        }

        public static string Cn(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "";
            string v;
            if (Map.TryGetValue(tag, out v)) return v;
            return tag;
        }

        /// <summary>搜索用键：英文 tag 与中文名都能命中，例如 "city 城市"。</summary>
        public static string SearchKey(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "";
            string cn = Cn(tag);
            if (cn == tag) return tag;
            return tag + " " + cn;
        }

        /// <summary>
        /// 反查：把用户输入的关键词（中英皆可）映射回图源用的英文 tag。
        /// 先精确匹配，再按关键词里的字眼模糊判断（"动漫壁纸"→anime、"唯美风景"→nature），
        /// 都判不出来才返回 "online"，至少保证新内容有归处，不会被当成空分类丢掉。
        /// </summary>
        public static string GuessTag(string keyword)
        {
            if (string.IsNullOrEmpty(keyword)) return "online";
            string k = keyword.Trim();

            foreach (KeyValuePair<string, string> kv in Map)
            {
                if (string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase)) return kv.Key;
                if (string.Equals(kv.Value, k, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            }

            string low = k.ToLowerInvariant();
            if (Has(low, "动漫", "二次元", "番剧", "anime", "漫画", "acg", "小姐姐", "插画")) return "anime";
            if (Has(low, "风景", "自然", "山水", "森林", "山", "nature", "landscape")) return "nature";
            if (Has(low, "城市", "都市", "街道", "city", "urban")) return "city";
            if (Has(low, "海", "浪", "ocean", "sea", "水下")) return "ocean";
            if (Has(low, "太空", "星空", "宇宙", "space", "galaxy", "星云")) return "space";
            if (Has(low, "雨", "rain", "夜")) return "rain";
            if (Has(low, "车", "car", "跑车")) return "car";
            if (Has(low, "游戏", "game", "原神", "英雄联盟", "lol")) return "game";
            return "online";
        }

        static bool Has(string text, params string[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
                if (text.IndexOf(keys[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}
