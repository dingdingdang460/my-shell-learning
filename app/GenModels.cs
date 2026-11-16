using System;
using System.Collections.Generic;
using System.Globalization;

namespace WallpaperManager
{
    /// <summary>
    /// 云端图生视频「模型目录」：可选模型、公开单价、典型等待时长。
    ///
    /// 为什么单独抽一个文件：主人问「用哪个模型、要等多久、大概花多少钱」，
    /// 这三个数要在三处同时出现且必须一致 —— 设置面板、下单确认浮条、等待中的浮条。
    /// 以前模型名只散在 GenApi.DefaultModel 和一个手填文本框里，价格只写在注释中，
    /// 界面上一分钱都看不到。现在统一由这里供数，改一处三处同步。
    ///
    /// 价格口径（2026-11-13 核实，均为服务商公开价，人民币）：
    ///   通义万相 / 阿里云百炼 help.aliyun.com/zh/model-studio/wan2-6-i2v-flash
    ///     wan2.6-i2v-flash  无声 720P ¥0.15/秒、1080P ¥0.25/秒（带声音分别 0.3 / 0.5）
    ///     wan2.6-i2v        720P ¥0.6/秒、1080P ¥1.0/秒
    ///     wan2.2-i2v-flash  无声 720P ¥0.20/秒、1080P ¥0.48/秒
    ///   火山方舟 / 火山引擎 console.volcengine.com/ark/.../model-pricing
    ///     按 token 计费，不是按秒：pro-fast ¥4.2/百万 token、pro ¥15/百万 token。
    ///     token ≈ 时长 × 宽 × 高 × 帧率 / 1024，按 24fps 折算成秒单价：
    ///       pro-fast  720P ≈ ¥0.09/秒、1080P ≈ ¥0.20/秒
    ///       pro       720P ≈ ¥0.32/秒、1080P ≈ ¥0.73/秒
    ///     这两行是折算值，实际以 usage.completion_tokens 账单为准，界面上会写明。
    ///
    /// 等待时长是经验区间（含排队），不是厂商 SLA；界面一律用「约」并标「以实际为准」。
    /// 目录只放**真实存在且官方列过 ID** 的模型，避免主人选到一个根本调不通的名字。
    /// </summary>
    public sealed class GenModelInfo
    {
        public string Id;
        public string Label;
        public GenApi.Provider Provider;
        public double Price720;      // 元 / 秒（720P）
        public double Price1080;     // 元 / 秒（1080P）
        public int WaitMinSec;       // 典型等待下限（秒）
        public int WaitMaxSec;       // 典型等待上限（秒）
        public bool IsDefault;
        public string Note;          // 价格口径备注（是否在账单上按 token 结算等）

        // 11-16：这家模型能接受的出片秒数区间。以前程序一律按 3 秒传，
        // 但通义部分模型固定 5 秒（wan2.2/2.1 系列），传 3 会被服务端拒；
        // 充值后第一次点生成就失败，体验很差。现在按模型夹取，下单参数一定合法。
        public int MinSeconds = 2;
        public int MaxSeconds = 15;

        /// <summary>取对应分辨率的秒单价；未知分辨率一律按 720P 算（宁可报低不报高）</summary>
        public double Price(string resolution)
        {
            return string.Equals(resolution, "1080P", StringComparison.OrdinalIgnoreCase)
                ? Price1080 : Price720;
        }

        /// <summary>用于「预计还需」倒计时的代表值：区间中值</summary>
        public int WaitTypicalSec { get { return (WaitMinSec + WaitMaxSec) / 2; } }
    }

    public static class GenModels
    {
        static readonly List<GenModelInfo> all = new List<GenModelInfo>();

        static GenModels()
        {
            Add("wan2.6-i2v-flash", "万相 2.6 Flash（快、最便宜）", GenApi.Provider.Wanx,
                0.15, 0.25, 60, 180, true, "官方按秒计价：720P 无声 ¥0.15/秒、1080P 无声 ¥0.25/秒",
                2, 15);
            Add("wan2.2-i2v-flash", "万相 2.2 Flash（老版、稍贵）", GenApi.Provider.Wanx,
                0.20, 0.48, 90, 240, false, "官方按秒计价：720P 无声 ¥0.20/秒、1080P 无声 ¥0.48/秒",
                5, 5);
            Add("wan2.6-i2v", "万相 2.6 标准（带声音、贵）", GenApi.Provider.Wanx,
                0.60, 1.00, 120, 300, false, "官方按秒计价：720P ¥0.6/秒、1080P ¥1.0/秒",
                2, 15);

            Add("doubao-seedance-1-0-pro-fast-251015", "Seedance 1.0 Pro Fast（效果接近参考片）",
                GenApi.Provider.Ark, 0.09, 0.20, 90, 300, true,
                "按 token 计费（¥4.2/百万 token），此处按 24fps 折算成秒价，以账单为准",
                3, 12);
            Add("doubao-seedance-1-0-pro-250528", "Seedance 1.0 Pro（更慢更贵）",
                GenApi.Provider.Ark, 0.32, 0.73, 180, 480, false,
                "按 token 计费（¥15/百万 token），此处按 24fps 折算成秒价，以账单为准",
                3, 12);
        }

        static void Add(string id, string label, GenApi.Provider p,
                        double p720, double p1080, int wmin, int wmax, bool def, string note,
                        int minSec, int maxSec)
        {
            GenModelInfo m = new GenModelInfo();
            m.Id = id;
            m.Label = label;
            m.Provider = p;
            m.Price720 = p720;
            m.Price1080 = p1080;
            m.WaitMinSec = wmin;
            m.WaitMaxSec = wmax;
            m.IsDefault = def;
            m.Note = note;
            m.MinSeconds = minSec;
            m.MaxSeconds = maxSec;
            all.Add(m);
        }

        public static List<GenModelInfo> All()
        {
            return new List<GenModelInfo>(all);
        }

        /// <summary>按服务商过滤；None = 没选服务商，返回全部供「看看有哪些/多少钱」</summary>
        public static List<GenModelInfo> For(GenApi.Provider p)
        {
            if (p == GenApi.Provider.None) return All();
            List<GenModelInfo> r = new List<GenModelInfo>();
            for (int i = 0; i < all.Count; i++) if (all[i].Provider == p) r.Add(all[i]);
            return r;
        }

        public static GenModelInfo Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < all.Count; i++)
                if (string.Equals(all[i].Id, id, StringComparison.OrdinalIgnoreCase)) return all[i];
            return null;
        }

        /// <summary>某服务商的默认模型。目录里没有就用通义 Flash 兜底，绝不返回空串。</summary>
        public static GenModelInfo DefaultFor(GenApi.Provider p)
        {
            List<GenModelInfo> list = For(p);
            for (int i = 0; i < list.Count; i++) if (list[i].IsDefault) return list[i];
            if (list.Count > 0) return list[0];
            return all[0];
        }

        /// <summary>
        /// 按「服务商 + 主人填的模型 ID」解析出目录项。
        /// 空 ID、手填了一个目录外的名字、或名字不属于当前服务商 → 一律回落到该服务商默认，
        /// 保证请求体里的 model 永远是一个能调通的值。
        /// </summary>
        public static GenModelInfo Resolve(GenApi.Provider p, string id)
        {
            GenModelInfo m = Find(id);
            if (m != null && (p == GenApi.Provider.None || m.Provider == p)) return m;
            return DefaultFor(p);
        }

        /// <summary>
        /// 把想要的秒数夹到这家模型真正接受的区间（纯函数，自检直接断言）。
        /// 例如 wan2.2-i2v-flash 固定 5 秒，传 3 会得到 5 —— 不夹的话服务端直接报参数错。
        /// </summary>
        public static int ClampSeconds(GenModelInfo m, int seconds)
        {
            if (m == null) return seconds < 1 ? 1 : seconds;
            if (seconds < m.MinSeconds) seconds = m.MinSeconds;
            if (seconds > m.MaxSeconds) seconds = m.MaxSeconds;
            return seconds;
        }

        /// <summary>费用估算（纯函数，自检可直接断言）：秒单价 × 实际会出片的秒数</summary>
        public static double Cost(GenModelInfo m, string resolution, int seconds)
        {
            if (m == null) return 0.0;
            seconds = ClampSeconds(m, seconds);
            if (seconds < 1) seconds = 1;
            return m.Price(resolution) * seconds;
        }

        /// <summary>「约 ¥0.45」——保留两位小数，末尾不带多余单位</summary>
        public static string CostText(GenModelInfo m, string resolution, int seconds)
        {
            double c = Cost(m, resolution, seconds);
            return "约 ¥" + c.ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>「约 1~3 分钟」；不足 1 分钟按秒写</summary>
        public static string WaitText(GenModelInfo m)
        {
            if (m == null) return "预计时间未知";
            return RangeText(m.WaitMinSec, m.WaitMaxSec);
        }

        public static string RangeText(int lo, int hi)
        {
            if (lo < 0) lo = 0;
            if (hi < lo) hi = lo;
            if (hi < 60) return "约 " + lo + "~" + hi + " 秒";
            int a = (int)Math.Round(lo / 60.0);
            int b = (int)Math.Round(hi / 60.0);
            if (a < 1) a = 1;
            if (b < a) b = a;
            if (a == b) return "约 " + a + " 分钟";
            return "约 " + a + "~" + b + " 分钟";
        }

        /// <summary>把秒数说成人话：12 秒 / 1 分 12 秒 / 2 小时 5 分</summary>
        public static string ElapsedText(long sec)
        {
            if (sec < 0) sec = 0;
            if (sec < 60) return sec + " 秒";
            if (sec < 3600) return (sec / 60) + " 分 " + (sec % 60) + " 秒";
            return (sec / 3600) + " 小时 " + ((sec % 3600) / 60) + " 分";
        }

        /// <summary>下拉项文本：一眼能看到模型、单价和大概等多久</summary>
        public static string ComboLabel(GenModelInfo m, string resolution, int seconds)
        {
            if (m == null) return "";
            double per = m.Price(resolution);
            int sec = ClampSeconds(m, seconds);
            return m.Label + "　｜" + per.ToString("0.00", CultureInfo.InvariantCulture)
                 + " 元/秒　｜" + WaitText(m) + "　｜" + sec + " 秒 " + CostText(m, resolution, sec);
        }
    }
}
