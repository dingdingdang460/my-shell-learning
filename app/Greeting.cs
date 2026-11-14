using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WallpaperManager
{
    /// <summary>
    /// 欢迎语文案。两份来源、一条取句顺序：
    ///   ① resources\greetings.json 的 per_id —— 对着画面手写的那批，最贴题；
    ///   ② %APPDATA%\wallpaper\greetings-auto.json —— 新图入库时 WelcomeGen 现写的；
    ///   ③ 都没有才按分类从 who 池挑主语套模板（在线来源、临时预览走这条）。
    /// 自动那份单独存，重装/重编译不会把生成结果冲掉，也不会动手工文案。
    /// </summary>
    public static class Greeting
    {
        static Dictionary<string, object> cfg;       // 打包进来的手工文案
        static Dictionary<string, object> autoPer;   // 自动生成的那份（可写）
        static string lastDir;                       // 记住资源目录，Reload 用

        public static string AutoPath
        {
            get { return Path.Combine(ConfigStore.Dir, "greetings-auto.json"); }
        }

        public static bool Load(string resourcesDir)
        {
            lastDir = resourcesDir;
            try
            {
                string f = Path.Combine(resourcesDir, "greetings.json");
                if (!File.Exists(f)) return false;
                cfg = Json.AsDict(Json.Parse(File.ReadAllText(f, Encoding.UTF8)));
                autoPer = LoadAuto();
                return cfg != null;
            }
            catch { return false; }
        }

        /// <summary>重新读一遍（自检验「写进文件 → 重启后读得回来」用）</summary>
        public static bool Reload()
        {
            return lastDir != null && Load(lastDir);
        }

        static Dictionary<string, object> LoadAuto()
        {
            try
            {
                if (!File.Exists(AutoPath)) return new Dictionary<string, object>();
                Dictionary<string, object> root = Json.AsDict(Json.Parse(File.ReadAllText(AutoPath, Encoding.UTF8)));
                Dictionary<string, object> per = Json.AsDict(Get(root, "per_id"));
                return per == null ? new Dictionary<string, object>() : per;
            }
            catch (Exception ex)
            {
                // 静默返回空会让「明明写了却读不到」变成悬案，这里必须留痕
                Log.Note("!! 自动欢迎语读不出来，先按空处理：" + ex.Message);
                return new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// 取一句欢迎语。顺序是：先看 per_id 里有没有为这张壁纸单独写的那句 ——
        /// 那些句子是对着画面里真实的元素写的（港口/雨窗/桥/猫/森林光斑），最贴题；
        /// 没有收录的（在线来源、刚入库的新壁纸）再按分类从 who 池里挑主语套模板。
        /// </summary>
        public static string Compose(string id, string tag, Random rnd)
        {
            string fallback = "欢迎回来，主人";
            if (cfg == null) return fallback;

            Random r = rnd == null ? new Random() : rnd;
            string own = PickPerId(id, r);
            if (own != null) return own;

            object poolObj = null;
            Dictionary<string, object> tagMap = Json.AsDict(Get(cfg, "tag_map"));
            Dictionary<string, object> who = Json.AsDict(Get(cfg, "who"));
            if (who == null) return fallback;

            string key = null;
            if (tagMap != null && !string.IsNullOrEmpty(tag))
            {
                object mapped;
                if (tagMap.TryGetValue(tag, out mapped) && mapped != null) key = Convert.ToString(mapped);
            }
            if (!string.IsNullOrEmpty(key)) who.TryGetValue(key, out poolObj);
            if (poolObj == null) who.TryGetValue("default", out poolObj);

            object[] pool = poolObj as object[];
            string subject = (pool != null && pool.Length > 0) ? Convert.ToString(pool[r.Next(pool.Length)]) : "主人";

            object[] templates = Json.AsArr(Get(cfg, "templates"));
            string tpl = (templates != null && templates.Length > 0)
                ? Convert.ToString(templates[r.Next(templates.Length)])
                : "欢迎回来，{who}";

            return tpl.Replace("{who}", subject);
        }

        /// <summary>这张壁纸有没有专属文案（手工或自动都算）</summary>
        public static bool HasPerId(string id)
        {
            object v;
            return TryGetLine(id, out v);
        }

        /// <summary>手工文案优先，其次自动生成的那条</summary>
        static bool TryGetLine(string id, out object v)
        {
            v = null;
            if (cfg == null || string.IsNullOrEmpty(id)) return false;

            Dictionary<string, object> per = Json.AsDict(Get(cfg, "per_id"));
            if (per != null && per.TryGetValue(id, out v) && v != null) return true;

            if (autoPer != null && autoPer.TryGetValue(id, out v) && v != null) return true;
            return false;
        }

        static string PickPerId(string id, Random r)
        {
            object v;
            if (!TryGetLine(id, out v)) return null;

            object[] lines = v as object[];
            if (lines != null && lines.Length > 0) return Convert.ToString(lines[r.Next(lines.Length)]);

            string one = v as string;
            return string.IsNullOrEmpty(one) ? null : one;
        }

        /// <summary>
        /// 保证这张壁纸有一句专属文案：没有就现写一条并落盘。
        /// 下载入库、云端出片、拖文件进库都会调这里 —— 新壁纸不会停在通用句上。
        /// 返回 true 表示这次真的新写了一条。
        /// </summary>
        public static bool Ensure(string id, string title, string tag, string color, out string line)
        {
            line = null;
            if (cfg == null || string.IsNullOrEmpty(id)) return false;
            if (HasPerId(id)) return false;
            if (autoPer == null) autoPer = new Dictionary<string, object>();

            line = WelcomeGen.Compose(title, tag, color, UsedLines(), null);
            if (string.IsNullOrEmpty(line)) { line = null; return false; }

            autoPer[id] = new object[] { line };
            SaveAuto();
            return true;
        }

        /// <summary>给库里所有还没有文案的壁纸批量补写，返回新写了几条</summary>
        public static int Backfill(IEnumerable<WallpaperItem> items)
        {
            if (items == null) return 0;
            int n = 0;
            foreach (WallpaperItem it in items)
            {
                if (it == null || HasPerId(it.Id)) continue;
                string path = !string.IsNullOrEmpty(it.ImagePath) ? it.ImagePath : it.ThumbAbs;
                string line;
                if (Ensure(it.Id, it.Title, it.Tag, ColorTag.Of(path), out line)) n++;
            }
            return n;
        }

        /// <summary>已经用过的句子：生成时避开，免得十几张壁纸说同一句</summary>
        static HashSet<string> UsedLines()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            CollectLines(set, Json.AsDict(Get(cfg, "per_id")));
            CollectLines(set, autoPer);
            return set;
        }

        static void CollectLines(HashSet<string> set, Dictionary<string, object> per)
        {
            if (per == null) return;
            foreach (KeyValuePair<string, object> kv in per)
            {
                object[] arr = kv.Value as object[];
                if (arr != null)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        string x = Convert.ToString(arr[i]);
                        if (!string.IsNullOrEmpty(x)) set.Add(x);
                    }
                    continue;
                }
                string one = kv.Value as string;
                if (!string.IsNullOrEmpty(one)) set.Add(one);
            }
        }

        /// <summary>自检用：删掉一条自动文案（手工文案删不到，也不该删）</summary>
        public static bool RemoveAuto(string id)
        {
            if (autoPer == null || string.IsNullOrEmpty(id)) return false;
            if (!autoPer.Remove(id)) return false;
            SaveAuto();
            // 一条不剩就把文件删掉，别在用户目录留个空壳
            if (autoPer.Count == 0) { try { File.Delete(AutoPath); } catch { } }
            return true;
        }

        static void SaveAuto()
        {
            try
            {
                Directory.CreateDirectory(ConfigStore.Dir);

                List<string> keys = new List<string>(autoPer.Keys);
                keys.Sort(StringComparer.Ordinal);     // 键排序：文件 diff 稳定，肉眼也好查

                // 说明里的路径带反斜杠，必须经 Json.Quote 转义；直接写进 JSON 会变成非法转义符，
                // 下次 LoadAuto 解析失败 → 整份自动文案读不回来（这条踩过）。
                string note = "自动生成的欢迎语（下载入库 / 云端出片时补写）。手工文案在 resources\\greetings.json；删掉本文件即可整批重新生成。";

                StringBuilder sb = new StringBuilder();
                sb.Append("{\r\n");
                sb.Append("  \"_说明\": ").Append(Json.Quote(note)).Append(",\r\n");
                sb.Append("  \"per_id\": {\r\n");
                for (int i = 0; i < keys.Count; i++)
                {
                    object v = autoPer[keys[i]];
                    object[] arr = v as object[];
                    if (arr == null) arr = new object[] { v };

                    sb.Append("    ").Append(Json.Quote(keys[i])).Append(": [");
                    for (int k = 0; k < arr.Length; k++)
                    {
                        if (k > 0) sb.Append(", ");
                        sb.Append(Json.Quote(Convert.ToString(arr[k])));
                    }
                    sb.Append("]").Append(i == keys.Count - 1 ? "" : ",").Append("\r\n");
                }
                sb.Append("  }\r\n}\r\n");

                File.WriteAllText(AutoPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Note("欢迎语落盘失败：" + ex.Message); }
        }

        static object Get(Dictionary<string, object> d, string k)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v)) return v;
            return null;
        }
    }

    /// <summary>全屏欢迎动画：淡入 → 停留 → 淡出，点击或按 Esc 可跳过</summary>
    public class GreetingWindow : Window
    {
        readonly DispatcherTimer tick;
        readonly Stopwatch clock = new Stopwatch();
        readonly FrameworkElement root;

        const double FadeIn = 0.55;   // 秒
        const double Hold = 1.85;
        const double FadeOut = 0.65;

        public GreetingWindow(string text, string subtitle)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;

            // WPF 硬限制：ShowActivated=false 时不允许 WindowState=Maximized，
            // Show() 会抛 InvalidOperationException 导致整块动画不出现。
            // 改成 Normal + 显式尺寸铺满主屏，同样能全屏，且不抢焦点。
            WindowState = WindowState.Normal;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            Border shade = new Border();
            shade.Background = new SolidColorBrush(Color.FromArgb(238, 6, 7, 10));

            StackPanel sp = new StackPanel();
            sp.HorizontalAlignment = HorizontalAlignment.Center;
            sp.VerticalAlignment = VerticalAlignment.Center;

            TextBlock main = new TextBlock();
            main.Text = text;
            main.FontSize = 46;
            main.FontWeight = FontWeights.Light;
            main.Foreground = new SolidColorBrush(Color.FromRgb(240, 241, 246));
            main.HorizontalAlignment = HorizontalAlignment.Center;
            main.FontFamily = new FontFamily("Microsoft YaHei UI");
            main.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 0,
                Opacity = 0.55,
                Color = Color.FromRgb(120, 160, 255)
            };

            TextBlock sub = new TextBlock();
            sub.Text = subtitle;
            sub.FontSize = 13;
            sub.Margin = new Thickness(0, 16, 0, 0);
            sub.Foreground = new SolidColorBrush(Color.FromRgb(120, 130, 155));
            sub.HorizontalAlignment = HorizontalAlignment.Center;
            sub.FontFamily = new FontFamily("Microsoft YaHei UI");

            sp.Children.Add(main);
            sp.Children.Add(sub);
            shade.Child = sp;
            shade.Opacity = 0;

            root = shade;
            Content = shade;

            MouseDown += delegate { Close(); };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); };

            tick = new DispatcherTimer();
            tick.Interval = TimeSpan.FromMilliseconds(16);
            tick.Tick += OnTick;

            Loaded += delegate
            {
                clock.Start();
                tick.Start();
            };
        }

        void OnTick(object sender, EventArgs e)
        {
            double t = clock.Elapsed.TotalSeconds;
            double o;
            if (t < FadeIn) o = t / FadeIn;
            else if (t < FadeIn + Hold) o = 1.0;
            else o = 1.0 - (t - FadeIn - Hold) / FadeOut;

            if (o < 0) o = 0;
            if (o > 1) o = 1;
            root.Opacity = o;

            if (t > FadeIn + Hold + FadeOut)
            {
                tick.Stop();
                Close();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            tick.Stop();
            base.OnClosed(e);
        }
    }
}
