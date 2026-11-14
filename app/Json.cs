using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace WallpaperManager
{
    /// <summary>用系统自带 JavaScriptSerializer 解析 JSON，避免引入第三方库</summary>
    public static class Json
    {
        static readonly JavaScriptSerializer Ser = CreateSer();

        static JavaScriptSerializer CreateSer()
        {
            JavaScriptSerializer s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 200;
            return s;
        }

        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            return Ser.DeserializeObject(text);
        }

        public static Dictionary<string, object> AsDict(object o)
        {
            return o as Dictionary<string, object>;
        }

        public static object[] AsArr(object o)
        {
            return o as object[];
        }

        /// <summary>
        /// 按路径取字符串，例如 PathStr(json, "output", "video_url")。
        /// 云端接口的返回都是几层嵌套，写一堆 AsDict(Str(...)) 既难看又容易空引用，统一走这里。
        /// 任何一层取不到都返回 ""，调用方不用判空。
        /// </summary>
        public static string PathStr(string json, params string[] keys)
        {
            try
            {
                object cur = Parse(json);
                for (int i = 0; i < keys.Length; i++)
                {
                    Dictionary<string, object> d = cur as Dictionary<string, object>;
                    if (d == null) return "";
                    object v;
                    if (!d.TryGetValue(keys[i], out v) || v == null) return "";
                    cur = v;
                }
                return cur is string ? (string)cur : Convert.ToString(cur, CultureInfo.InvariantCulture);
            }
            catch { return ""; }
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            if (d == null) return "";
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return "";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static long Long(Dictionary<string, object> d, string key)
        {
            if (d == null) return 0;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        public static double Dbl(Dictionary<string, object> d, string key)
        {
            if (d == null) return 0;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool fallback)
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        /// <summary>把对象序列化回 JSON（删条目时重写 index.json 用）</summary>
        public static string Serialize(object o)
        {
            return Ser.Serialize(o);
        }

        /// <summary>把字符串转成合法 JSON 字面量（处理反斜杠与引号）</summary>
        public static string Quote(string s)
        {
            if (s == null) s = "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
