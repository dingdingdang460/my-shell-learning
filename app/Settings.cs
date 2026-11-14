using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace WallpaperManager
{
    /// <summary>开机自启：只写 HKCU 的 Run 键，不需要管理员权限</summary>
    public static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "WallpaperManager";

        public static bool IsOn()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (k == null) return false;
                    return k.GetValue(ValueName) != null;
                }
            }
            catch { return false; }
        }

        public static bool Apply(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return false;
                    if (on)
                    {
                        string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
                        k.SetValue(ValueName, "\"" + exe + "\"");
                    }
                    else if (k.GetValue(ValueName) != null)
                    {
                        k.DeleteValue(ValueName, false);
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Note("开机自启写入失败：" + ex.Message);
                return false;
            }
        }
    }

    /// <summary>设置面板：库路径、轮换、开机自启、欢迎动画、竖图过滤、动态壁纸帧率</summary>
    public class SettingsWindow : Window
    {
        readonly App app;
        readonly TextBox libBox = new TextBox();
        readonly TextBox intervalBox = new TextBox();
        readonly TextBox fpsBox = new TextBox();
        readonly CheckBox autoRotateBox = new CheckBox();
        readonly CheckBox autoStartBox = new CheckBox();
        readonly CheckBox welcomeBootBox = new CheckBox();
        readonly CheckBox welcomeUnlockBox = new CheckBox();
        readonly CheckBox filterPortraitBox = new CheckBox();
        readonly TextBox proxyBox = new TextBox();
        readonly TextBox cacheBox = new TextBox();
        readonly TextBlock cacheInfo = new TextBlock();
        readonly ComboBox provBox = new ComboBox();
        readonly PasswordBox keyBox = new PasswordBox();
        readonly ComboBox modelCombo = new ComboBox();    // 模型改成下拉：看得见单价和大概等多久
        readonly ComboBox resBox = new ComboBox();
        readonly TextBlock genEstimate = new TextBlock(); // 「本次约 ¥X · 预计 Y」实时预估
        // genCmd 只保留字段做老配置兼容，不再有 UI 入口、也不会被拉起（脚本路线已放弃）
        readonly TextBox genCmdBox = new TextBox();
        ScrollViewer scroller;
        Button expBtn;                       // 导出/导入按钮：自检要能真实触发它们的 Click
        Button impBtn;

        // ---- 自检注入点：默认全部关闭，生产路径行为完全不变 ----
        // --importexporttest 临时设这些静态字段，让「导出…/导入…」按钮走真实
        // Click 处理器，但不弹系统文件对话框与 MessageBox（编辑器会抢前台）。
        internal static string ExportPick = null;
        internal static string ImportPick = null;
        internal static bool SuppressDialogs = false;
        internal static MessageBoxResult AutoAnswer = MessageBoxResult.OK;

        static readonly Brush TextMain = new SolidColorBrush(Color.FromRgb(232, 234, 240));
        static readonly Brush TextDim = new SolidColorBrush(Color.FromRgb(132, 138, 152));
        static readonly Brush BgInput = new SolidColorBrush(Color.FromRgb(30, 31, 38));
        static readonly Brush BorderIdle = new SolidColorBrush(Color.FromRgb(42, 44, 52));

        public SettingsWindow(App app)
        {
            this.app = app;

            Title = "设置";
            Width = 560;
            Height = 880;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(20, 21, 27));
            Foreground = TextMain;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            UseLayoutRounding = true;

            BuildUi();
            LoadFrom(app.Config);
        }

        void BuildUi()
        {
            StackPanel root = new StackPanel();
            root.Margin = new Thickness(26, 18, 26, 14);

            TextBlock head = new TextBlock();
            head.Text = "设置";
            head.FontSize = 20;
            head.FontWeight = FontWeights.SemiBold;
            head.Foreground = TextMain;
            root.Children.Add(head);

            TextBlock sub = new TextBlock();
            sub.Text = "改完点保存立即生效，配置存在 %APPDATA%\\wallpaper\\config.json";
            sub.FontSize = 11.5;
            sub.Margin = new Thickness(0, 4, 0, 18);
            sub.Foreground = TextDim;
            root.Children.Add(sub);

            // 壁纸库
            root.Children.Add(Label("壁纸库目录"));
            Grid libRow = new Grid();
            libRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            libRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StyleInput(libBox);
            libRow.Children.Add(libBox);

            Button browse = MakeButton("浏览…");
            browse.Margin = new Thickness(8, 0, 0, 0);
            browse.Click += delegate
            {
                using (Forms.FolderBrowserDialog dlg = new Forms.FolderBrowserDialog())
                {
                    dlg.Description = "选择壁纸库根目录（里面应有 images / videos / thumbs）";
                    if (Directory.Exists(libBox.Text)) dlg.SelectedPath = libBox.Text;
                    if (dlg.ShowDialog() == Forms.DialogResult.OK) libBox.Text = dlg.SelectedPath;
                }
            };
            Grid.SetColumn(browse, 1);
            libRow.Children.Add(browse);
            root.Children.Add(libRow);

            // 轮换
            root.Children.Add(Label("自动轮换（分钟）"));
            StyleInput(intervalBox);
            intervalBox.Width = 120;
            intervalBox.HorizontalAlignment = HorizontalAlignment.Left;
            root.Children.Add(intervalBox);

            StyleCheck(autoRotateBox, "启用定时轮换");
            root.Children.Add(autoRotateBox);

            // 开机自启 & 欢迎
            StyleCheck(autoStartBox, "开机自动启动（写入 HKCU 的 Run 键，不需要管理员）");
            root.Children.Add(autoStartBox);

            StyleCheck(welcomeBootBox, "开机后播一次欢迎动画");
            root.Children.Add(welcomeBootBox);

            StyleCheck(welcomeUnlockBox, "每次锁屏解锁后播欢迎动画");
            root.Children.Add(welcomeUnlockBox);

            StyleCheck(filterPortraitBox, "B 站图集里过滤竖版图（默认关，竖图会自动适配屏幕）");
            root.Children.Add(filterPortraitBox);

            // 动态壁纸
            root.Children.Add(Label("动态壁纸帧率上限（15-60）"));
            StyleInput(fpsBox);
            fpsBox.Width = 120;
            fpsBox.HorizontalAlignment = HorizontalAlignment.Left;
            root.Children.Add(fpsBox);

            // ---------------- 缓存 ----------------
            root.Children.Add(Separator());
            root.Children.Add(SectionHead("缓存"));

            root.Children.Add(Label("合成图缓存上限（MB）"));
            Grid cacheRow = new Grid();
            cacheRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cacheRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            StyleInput(cacheBox);
            cacheBox.Width = 120;
            cacheBox.HorizontalAlignment = HorizontalAlignment.Left;
            cacheRow.Children.Add(cacheBox);

            Button cleanCache = MakeButton("立即清理");
            cleanCache.Margin = new Thickness(10, 0, 0, 0);
            cleanCache.Click += delegate
            {
                int n = WallpaperFit.TrimTo(0);
                UpdateCacheInfo();
                app.SetStatusSafe("已清理合成图缓存 " + n + " 个文件");
            };
            Grid.SetColumn(cleanCache, 1);
            cleanCache.HorizontalAlignment = HorizontalAlignment.Left;
            cacheRow.Children.Add(cleanCache);
            root.Children.Add(cacheRow);

            cacheInfo.FontSize = 11.5;
            cacheInfo.Foreground = TextDim;
            cacheInfo.Margin = new Thickness(0, 6, 0, 0);
            root.Children.Add(cacheInfo);
            UpdateCacheInfo();

            // ---------------- 网络 ----------------
            root.Children.Add(Separator());
            root.Children.Add(SectionHead("网络"));

            root.Children.Add(Label("HTTP 代理（留空 = 直连）"));
            StyleInput(proxyBox);
            proxyBox.ToolTip = "形如 127.0.0.1:7890 或 http://127.0.0.1:7890。\n"
                             + "以前所有请求都是硬编码直连，这个框填空就保持原来的行为。";
            root.Children.Add(proxyBox);

            TextBlock proxyHint = new TextBlock();
            proxyHint.Text = "只影响本程序自己的联网（搜图、下载、B 站、云端生成）。改完点保存立即生效。";
            proxyHint.FontSize = 11.5;
            proxyHint.Foreground = TextDim;
            proxyHint.TextWrapping = TextWrapping.Wrap;
            proxyHint.Margin = new Thickness(0, 6, 0, 0);
            root.Children.Add(proxyHint);

            // ---------------- 配置导入导出 ----------------
            root.Children.Add(Separator());
            root.Children.Add(SectionHead("配置备份"));

            TextBlock cfgHint = new TextBlock();
            cfgHint.Text = "配置存在 %APPDATA%\\wallpaper\\config.json，换电脑时导出一份带走即可"
                         + "（壁纸库本身在 libRoot 目录里，要另外拷贝）。";
            cfgHint.FontSize = 11.5;
            cfgHint.Foreground = TextDim;
            cfgHint.TextWrapping = TextWrapping.Wrap;
            root.Children.Add(cfgHint);

            StackPanel cfgRow = new StackPanel();
            cfgRow.Orientation = Orientation.Horizontal;
            cfgRow.Margin = new Thickness(0, 8, 0, 0);

            expBtn = MakeButton("导出配置…");
            expBtn.Click += delegate { ExportConfig(); };
            impBtn = MakeButton("导入配置…");
            impBtn.Margin = new Thickness(10, 0, 0, 0);
            impBtn.Click += delegate { ImportConfig(); };
            cfgRow.Children.Add(expBtn);
            cfgRow.Children.Add(impBtn);
            root.Children.Add(cfgRow);

            // ---------------- 云端生成（只走图生视频模型） ----------------
            Border line = new Border();
            line.Height = 1;
            line.Background = BorderIdle;
            line.Margin = new Thickness(0, 22, 0, 0);
            root.Children.Add(line);

            TextBlock genHead = new TextBlock();
            genHead.Text = "云端生成（只用图生视频模型）";
            genHead.FontSize = 14;
            genHead.FontWeight = FontWeights.SemiBold;
            genHead.Foreground = TextMain;
            genHead.Margin = new Thickness(0, 16, 0, 0);
            root.Children.Add(genHead);

            TextBlock genHint = new TextBlock();
            genHint.Text = "右键「云端生成」→ 先看用哪个模型、预计等多久、大概花多少钱 → 确认后应用直连服务商"
                         + "提交、轮询、下载，到货自动换上桌面。\n"
                         + "本地脚本出片已停用：它只能挪动已有像素，做不出头发飘动这类内容级动态。\n"
                         + "费用按服务商公开单价估算，火山方舟按 token 折算，实际以账单为准。";
            genHint.FontSize = 11.5;
            genHint.LineHeight = 19;
            genHint.TextWrapping = TextWrapping.Wrap;
            genHint.Foreground = TextDim;
            genHint.Margin = new Thickness(0, 6, 0, 0);
            root.Children.Add(genHint);

            root.Children.Add(Label("服务商"));
            provBox.Items.Add("不用云端模型（点生成会提示先配置）");
            provBox.Items.Add("通义万相 DashScope（按秒计价，最便宜）");
            provBox.Items.Add("火山方舟 Ark / Seedance（按 token 计价，效果更接近参考片）");
            provBox.SelectedIndex = 0;
            provBox.SelectionChanged += delegate { RefreshModels(false); };
            StyleCombo(provBox);
            root.Children.Add(provBox);

            root.Children.Add(Label("API Key"));
            keyBox.Background = BgInput;
            keyBox.Foreground = TextMain;
            keyBox.CaretBrush = TextMain;
            keyBox.BorderBrush = BorderIdle;
            keyBox.BorderThickness = new Thickness(1);
            keyBox.Padding = new Thickness(9, 7, 9, 7);
            keyBox.FontSize = 13;
            keyBox.PasswordChar = '●';
            keyBox.ToolTip = "在阿里云百炼 / 火山方舟控制台开通图生视频后拿到的 Key，只存在本机 config.json";
            root.Children.Add(keyBox);

            root.Children.Add(Label("模型（单价 / 预计等待 / 3 秒总价）"));
            StyleCombo(modelCombo);
            modelCombo.SelectionChanged += delegate { UpdateEstimate(); };
            root.Children.Add(modelCombo);

            root.Children.Add(Label("出片分辨率"));
            resBox.Items.Add("720P（省额度）");
            resBox.Items.Add("1080P（更清楚，更贵）");
            resBox.SelectedIndex = 0;
            resBox.SelectionChanged += delegate { RefreshModels(true); };
            StyleCombo(resBox);
            root.Children.Add(resBox);

            genEstimate.FontSize = 12.5;
            genEstimate.FontWeight = FontWeights.SemiBold;
            genEstimate.Foreground = new SolidColorBrush(Color.FromRgb(150, 214, 176));
            genEstimate.TextWrapping = TextWrapping.Wrap;
            genEstimate.LineHeight = 20;
            genEstimate.Margin = new Thickness(0, 12, 0, 0);
            root.Children.Add(genEstimate);

            TextBlock genPriceHint = new TextBlock();
            genPriceHint.Text = "价格来源：阿里云百炼 wan2.6-i2v-flash 无声 720P ¥0.15/秒、1080P ¥0.25/秒；"
                              + "火山方舟按 token 计费（pro-fast ¥4.2/百万、pro ¥15/百万），此处按 24fps 折算成秒价。"
                              + "预计等待为经验区间（含排队），不是厂商承诺。2026-11-13 核实。";
            genPriceHint.FontSize = 10.5;
            genPriceHint.LineHeight = 17;
            genPriceHint.TextWrapping = TextWrapping.Wrap;
            genPriceHint.Foreground = TextDim;
            genPriceHint.Margin = new Thickness(0, 6, 0, 0);
            root.Children.Add(genPriceHint);

            RefreshModels(false);
            UpdateEstimate();

            // 底部按钮
            StackPanel btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            btns.HorizontalAlignment = HorizontalAlignment.Right;
            btns.Margin = new Thickness(0, 22, 0, 0);

            Button save = MakeButton("保存");
            save.Padding = new Thickness(24, 8, 24, 8);
            save.Background = new SolidColorBrush(Color.FromRgb(46, 92, 168));
            save.BorderBrush = new SolidColorBrush(Color.FromRgb(64, 118, 208));
            save.Foreground = Brushes.White;
            save.Click += delegate { Save(); };

            Button cancel = MakeButton("取消");
            cancel.Margin = new Thickness(10, 0, 0, 0);
            cancel.Padding = new Thickness(20, 8, 20, 8);
            cancel.Click += delegate { Close(); };

            btns.Children.Add(save);
            btns.Children.Add(cancel);

            ScrollViewer sv = new ScrollViewer();
            scroller = sv;
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            // 必须关掉横向滚动：ScrollViewer 默认会用「无限宽」去量内容，
            // 于是 TextWrapping 的说明文字永远不换行，直接横着长到窗口外面去。
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            sv.Content = root;

            // 保存 / 取消 钉在窗口底部，不跟着内容滚 —— 否则加了「云端生成」那几栏之后，
            // 窗口一小就要滚到底才够得着保存按钮。
            btns.Margin = new Thickness(26, 10, 26, 16);
            DockPanel dock = new DockPanel();
            DockPanel.SetDock(btns, Dock.Bottom);
            dock.Children.Add(btns);
            dock.Children.Add(sv);
            Content = dock;
        }

        /// <summary>自检用：把设置面板滚到底，方便核对「云端生成」那一段的布局</summary>
        public void ScrollToEndForTest()
        {
            if (scroller != null) scroller.ScrollToEnd();
        }

        // ---------------- 云端模型选择 / 费用预估 ----------------

        GenApi.Provider CurrentProvider()
        {
            if (provBox.SelectedIndex == 1) return GenApi.Provider.Wanx;
            if (provBox.SelectedIndex == 2) return GenApi.Provider.Ark;
            return GenApi.Provider.None;
        }

        string CurrentResolution()
        {
            return resBox.SelectedIndex == 1 ? "1080P" : "720P";
        }

        /// <summary>下拉里选中的模型；没选就取该服务商默认，不返回 null 导致界面空白</summary>
        GenModelInfo SelectedModel()
        {
            List<GenModelInfo> list = GenModels.For(CurrentProvider());
            int i = modelCombo.SelectedIndex;
            if (i >= 0 && i < list.Count) return list[i];
            return list.Count > 0 ? list[0] : null;
        }

        /// <summary>
        /// 重建模型下拉。keepSelection=true 用于「切分辨率」——单价变了要重画标签，
        /// 但不能把主人刚选好的模型重置回默认。
        /// </summary>
        void RefreshModels(bool keepSelection)
        {
            string keep = "";
            if (keepSelection)
            {
                GenModelInfo cur = SelectedModel();
                if (cur != null) keep = cur.Id;
            }

            GenApi.Provider p = CurrentProvider();
            string res = CurrentResolution();
            List<GenModelInfo> list = GenModels.For(p);
            modelCombo.Items.Clear();
            for (int i = 0; i < list.Count; i++)
                modelCombo.Items.Add(GenModels.ComboLabel(list[i], res, CloudGen.MaxSeconds));

            string wantedId;
            if (keep.Length > 0) wantedId = keep;
            else wantedId = GenModels.Resolve(p, app == null || app.Config == null ? "" : app.Config.GenModel).Id;

            int idx = 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Id == wantedId) { idx = i; break; }
            if (modelCombo.Items.Count > 0) modelCombo.SelectedIndex = idx;
            UpdateEstimate();
        }

        void SelectModelById(string id)
        {
            GenApi.Provider p = CurrentProvider();
            List<GenModelInfo> list = GenModels.For(p);
            string want = GenModels.Resolve(p, id).Id;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Id == want) { modelCombo.SelectedIndex = i; return; }
            if (list.Count > 0) modelCombo.SelectedIndex = 0;
        }

        /// <summary>「本次大概花费 / 预计等待」——设置页、确认浮条、等待浮条用同一套算法</summary>
        void UpdateEstimate()
        {
            GenModelInfo m = SelectedModel();
            if (m == null) { genEstimate.Text = ""; return; }
            int sec = CloudGen.MaxSeconds;
            if (app != null && app.Config != null && app.Config.GenSeconds >= 1
                && app.Config.GenSeconds < sec) sec = app.Config.GenSeconds;
            string res = CurrentResolution();
            genEstimate.Text = "本次生成：" + sec + " 秒 · " + res + " · " + m.Label + "\n"
                + "大概花费 " + GenModels.CostText(m, res, sec)
                + "（" + m.Price(res).ToString("0.00", CultureInfo.InvariantCulture) + " 元/秒）"
                + "　预计等待 " + GenModels.WaitText(m);
        }

        /// <summary>
        /// 自检用（--cloudcfgtest）：真实操作设置面板的控件，核对模型联动、费用计算、
        /// 1080P 加价、配置落盘往返。全程只动新建的自检窗口，不改 app.Config。
        /// </summary>
        public string SelfTestCloudModels()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            bool ok = true;

            provBox.SelectedIndex = 1;      // 通义
            bool wanx = modelCombo.Items.Count == 3
                     && SelectedModel() != null
                     && SelectedModel().Id == "wan2.6-i2v-flash";
            if (!wanx) ok = false;
            sb.Append("通义模型下拉 3 项 + 默认 Flash ").Append(wanx ? "√" : "×").Append("\r\n");

            GenModelInfo flash = SelectedModel();
            double c720 = GenModels.Cost(flash, "720P", 3);
            bool costOk = Math.Abs(c720 - 0.45) < 0.0001 && genEstimate.Text.IndexOf("约 ¥0.45") >= 0
                       && genEstimate.Text.IndexOf("预计等待") >= 0;
            if (!costOk) ok = false;
            sb.Append("720P 3 秒 = ¥0.45 且预估文案含金额/等待 ").Append(costOk ? "√" : "×").Append("\r\n");

            resBox.SelectedIndex = 1;       // 切 1080P，模型不能被重置、单价要变
            bool resOk = SelectedModel() != null && SelectedModel().Id == "wan2.6-i2v-flash"
                      && Math.Abs(GenModels.Cost(flash, "1080P", 3) - 0.75) < 0.0001;
            if (!resOk) ok = false;
            sb.Append("1080P 加价到 ¥0.75 且模型保持 ").Append(resOk ? "√" : "×").Append("\r\n");
            resBox.SelectedIndex = 0;

            provBox.SelectedIndex = 2;      // 火山
            bool ark = modelCombo.Items.Count == 2
                    && SelectedModel() != null
                    && SelectedModel().Id == "doubao-seedance-1-0-pro-fast-251015";
            if (!ark) ok = false;
            sb.Append("火山模型下拉 2 项 + 默认 pro-fast ").Append(ark ? "√" : "×").Append("\r\n");

            // 配置落盘往返：模型 ID / 服务商 / 分辨率必须原样回来
            string tmp = Path.Combine(Path.GetTempPath(), "wallpaper-cloudcfg-roundtrip.json");
            bool round = false;
            try
            {
                AppConfig t = new AppConfig();
                t.GenProvider = "ark";
                t.GenApiKey = "MOCK-KEY-NOT-REAL";
                t.GenModel = "doubao-seedance-1-0-pro-250528";
                t.GenResolution = "1080P";
                bool wrote = ConfigStore.WriteTo(t, tmp);
                AppConfig back = wrote ? ConfigStore.LoadFrom(tmp) : null;
                round = wrote && back != null && back.GenProvider == "ark"
                     && back.GenModel == "doubao-seedance-1-0-pro-250528"
                     && back.GenResolution == "1080P";
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (Exception ex) { sb.Append("往返异常 ").Append(ex.Message).Append("\r\n"); }
            if (!round) ok = false;
            sb.Append("genProvider/genModel/genResolution 落盘往返 ").Append(round ? "√" : "×").Append("\r\n");

            sb.Append(ok ? "PASS" : "FAIL");
            return sb.ToString();
        }

        TextBlock Label(string text)
        {
            TextBlock tb = new TextBlock();
            tb.Text = text;
            tb.FontSize = 12.5;
            tb.Foreground = TextDim;
            tb.Margin = new Thickness(0, 13, 0, 5);
            return tb;
        }

        void StyleInput(TextBox tb)
        {
            tb.Background = BgInput;
            tb.Foreground = TextMain;
            tb.CaretBrush = TextMain;
            tb.BorderBrush = BorderIdle;
            tb.BorderThickness = new Thickness(1);
            tb.Padding = new Thickness(9, 7, 9, 7);
            tb.FontSize = 13;
        }

        void StyleCheck(CheckBox cb, string text)
        {
            cb.Content = text;
            cb.FontSize = 12.5;
            cb.Foreground = TextMain;
            cb.Margin = new Thickness(0, 9, 0, 0);
        }

        void StyleCombo(ComboBox cb)
        {
            // 下拉框走系统模板（白底），所以前景色必须是深色。
            // 之前跟着其它输入框设成浅色，结果白底白字，选中项整段看不见。
            cb.Background = Brushes.White;
            cb.Foreground = new SolidColorBrush(Color.FromRgb(28, 29, 34));
            cb.BorderBrush = new SolidColorBrush(Color.FromRgb(120, 124, 136));
            cb.BorderThickness = new Thickness(1);
            cb.Padding = new Thickness(7, 5, 7, 5);
            cb.FontSize = 12.5;
            cb.HorizontalAlignment = HorizontalAlignment.Left;
            cb.MinWidth = 360;
            cb.MaxDropDownHeight = 240;
        }

        Button MakeButton(string text)
        {
            Button b = new Button();
            b.Content = text;
            b.FontSize = 12.5;
            b.Padding = new Thickness(14, 7, 14, 7);
            b.Background = new SolidColorBrush(Color.FromRgb(34, 36, 44));
            b.Foreground = TextMain;
            b.BorderBrush = BorderIdle;
            b.BorderThickness = new Thickness(1);
            b.Cursor = System.Windows.Input.Cursors.Hand;
            return b;
        }

        /// <summary>分组上方的分隔线</summary>
        Border Separator()
        {
            Border line = new Border();
            line.Height = 1;
            line.Background = BorderIdle;
            line.Margin = new Thickness(0, 22, 0, 0);
            return line;
        }

        TextBlock SectionHead(string text)
        {
            TextBlock tb = new TextBlock();
            tb.Text = text;
            tb.FontSize = 14;
            tb.FontWeight = FontWeights.SemiBold;
            tb.Foreground = TextMain;
            tb.Margin = new Thickness(0, 16, 0, 0);
            return tb;
        }

        void UpdateCacheInfo()
        {
            long bytes = WallpaperFit.CacheBytes();
            cacheInfo.Text = "当前占用 " + (bytes / 1024 / 1024) + " MB（" + WallpaperFit.CacheDir() + "）";
        }

        /// <summary>导出：先把当前内存里的配置落盘，再复制一份出去</summary>
        void ExportConfig()
        {
            string target;
            if (ExportPick != null)
            {
                target = ExportPick;
            }
            else
            {
                Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog();
                dlg.Filter = "配置文件 (*.json)|*.json|所有文件 (*.*)|*.*";
                dlg.FileName = "wallpaper-config.json";
                if (dlg.ShowDialog() != true) return;
                target = dlg.FileName;
            }
            try
            {
                ConfigStore.Save(app.Config);
                File.Copy(ConfigStore.FilePath, target, true);
                Log.Note("配置已导出：" + target);
                if (!SuppressDialogs)
                    MessageBox.Show("已导出到：\r\n" + target, "壁纸管家",
                                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log.Note("配置导出失败：" + ex.Message);
                if (!SuppressDialogs)
                    MessageBox.Show("导出失败：" + ex.Message, "壁纸管家",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---- 自检钩子：真实触发两个按钮的 Click（RaiseEvent 走同一个处理器）----
        public void SelfTestExportClick()
        {
            expBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        public void SelfTestImportClick()
        {
            impBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        /// <summary>把界面输入框当前值吐出来，核对导入后有没有刷新</summary>
        public string SelfTestFields()
        {
            return "interval=" + intervalBox.Text
                 + " proxy=" + proxyBox.Text
                 + " cache=" + cacheBox.Text
                 + " lib=" + libBox.Text;
        }

        /// <summary>
        /// 导入：先校验是本程序的配置，再覆盖 config.json 并重新读回来。
        /// 覆盖前留一份 .bak —— 导入错文件时还能换回去，不至于把库路径弄丢。
        /// </summary>
        void ImportConfig()
        {
            string source;
            if (ImportPick != null)
            {
                source = ImportPick;
            }
            else
            {
                Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
                dlg.Filter = "配置文件 (*.json)|*.json|所有文件 (*.*)|*.*";
                if (dlg.ShowDialog() != true) return;
                source = dlg.FileName;
            }

            string text;
            try { text = File.ReadAllText(source, System.Text.Encoding.UTF8); }
            catch (Exception ex)
            {
                if (!SuppressDialogs)
                    MessageBox.Show("读不了这个文件：" + ex.Message, "壁纸管家",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 11-14 修：原来只判断「JSON 能不能解析」，两个真实问题：
            //   ① 坏文件让 JavaScriptSerializer 直接抛异常，没人接 → 未处理异常；
            //   ② {"a":1} 这种合法 JSON 会被接受，LoadFrom 把缺字段全填默认值，
            //      库路径 / 代理 / 轮换等设置被静默清空。
            // 现在要求解析成功且带非空 libRoot（ConfigStore.WriteTo 必定写它）。
            bool valid = false;
            try
            {
                Dictionary<string, object> d = Json.AsDict(Json.Parse(text));
                valid = d != null && Json.Str(d, "libRoot").Length > 0;
            }
            catch { valid = false; }
            if (!valid)
            {
                if (!SuppressDialogs)
                    MessageBox.Show("这个文件不是壁纸管家的配置（读不出 libRoot 字段）。", "壁纸管家",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBoxResult r = SuppressDialogs
                ? AutoAnswer
                : MessageBox.Show(
                    "用这份配置覆盖当前设置？\r\n\r\n会改变库目录、轮换、代理等全部选项，"
                    + "旧配置会留一份 config.json.bak。",
                    "导入配置", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (r != MessageBoxResult.OK) return;

            try
            {
                if (File.Exists(ConfigStore.FilePath))
                    File.Copy(ConfigStore.FilePath, ConfigStore.FilePath + ".bak", true);
                File.Copy(source, ConfigStore.FilePath, true);
            }
            catch (Exception ex)
            {
                if (!SuppressDialogs)
                    MessageBox.Show("写入配置失败：" + ex.Message, "壁纸管家",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string oldRoot = app.Config.LibRoot;
            app.Config = ConfigStore.Load();
            Net.Configure(app.Config.Proxy);
            LoadFrom(app.Config);
            app.ApplySettings(!string.Equals(oldRoot, app.Config.LibRoot, StringComparison.OrdinalIgnoreCase));
            Log.Note("配置已导入：" + source);
            app.SetStatusSafe("已导入配置");
        }

        void LoadFrom(AppConfig c)
        {
            libBox.Text = c.LibRoot;
            intervalBox.Text = c.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
            fpsBox.Text = c.DynamicFps.ToString(CultureInfo.InvariantCulture);
            autoRotateBox.IsChecked = c.AutoRotate;
            autoStartBox.IsChecked = AutoStart.IsOn();
            welcomeBootBox.IsChecked = c.WelcomeOnBoot;
            welcomeUnlockBox.IsChecked = c.WelcomeOnUnlock;
            filterPortraitBox.IsChecked = c.FilterPortrait;
            proxyBox.Text = c.Proxy == null ? "" : c.Proxy;
            cacheBox.Text = c.CacheLimitMb.ToString(CultureInfo.InvariantCulture);

            // 云端模型配置：先切服务商（会重建模型下拉），再按保存的 ID 精确选中
            int pi = 0;
            if (c.GenProvider == "wanx") pi = 1;
            else if (c.GenProvider == "ark") pi = 2;
            provBox.SelectedIndex = pi;
            keyBox.Password = c.GenApiKey == null ? "" : c.GenApiKey;
            resBox.SelectedIndex = c.GenResolution == "1080P" ? 1 : 0;
            SelectModelById(c.GenModel);
            genCmdBox.Text = c.GenCmd == null ? "" : c.GenCmd;   // 老配置兼容保留，不再有 UI 入口
            UpdateEstimate();
        }

        void Save()
        {
            AppConfig c = app.Config;
            string newRoot = libBox.Text.Trim();
            bool rootChanged = !string.Equals(newRoot, c.LibRoot, StringComparison.OrdinalIgnoreCase);

            int iv = 0;
            if (!int.TryParse(intervalBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out iv) || iv < 1)
                iv = 180;

            int fps = 0;
            if (!int.TryParse(fpsBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out fps)) fps = 30;
            if (fps < 15) fps = 15;
            if (fps > 60) fps = 60;

            c.IntervalMinutes = iv;
            c.DynamicFps = fps;
            c.AutoRotate = autoRotateBox.IsChecked == true;
            c.WelcomeOnBoot = welcomeBootBox.IsChecked == true;
            c.WelcomeOnUnlock = welcomeUnlockBox.IsChecked == true;
            c.FilterPortrait = filterPortraitBox.IsChecked == true;
            c.AutoStart = autoStartBox.IsChecked == true;

            // 代理：空 = 直连（保持以前的行为）。填错的地址不会炸，只是请求走不通，
            // 所以这里不拦，改完点保存即可，界面上不会弹「格式不对」烦人。
            c.Proxy = proxyBox.Text == null ? "" : proxyBox.Text.Trim();
            Net.Configure(c.Proxy);

            int cacheMb = 0;
            if (!int.TryParse(cacheBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out cacheMb))
                cacheMb = 300;
            if (cacheMb < 50) cacheMb = 50;
            if (cacheMb > 20000) cacheMb = 20000;
            c.CacheLimitMb = cacheMb;
            int dropped = WallpaperFit.TrimTo(cacheMb * 1024L * 1024L);
            if (dropped > 0) Log.Note("缓存超上限，已清掉 " + dropped + " 张合成图");

            // 云端模型：服务商 + Key + 目录里的模型 ID + 分辨率，四件套一起存
            c.GenProvider = provBox.SelectedIndex == 1 ? "wanx" : (provBox.SelectedIndex == 2 ? "ark" : "");
            c.GenApiKey = keyBox.Password == null ? "" : keyBox.Password.Trim();
            GenModelInfo sel = SelectedModel();
            c.GenModel = sel == null ? "" : sel.Id;
            c.GenResolution = resBox.SelectedIndex == 1 ? "1080P" : "720P";
            c.GenCmd = genCmdBox.Text == null ? "" : genCmdBox.Text.Trim();
            if (c.GenProvider.Length > 0 && c.GenApiKey.Length == 0)
            {
                MessageBox.Show("选了服务商但还没填 API Key，云端生成现在用不了。\n\n"
                              + "请到阿里云百炼 / 火山方舟控制台开通图生视频并拿到 Key，填进上面这一栏。"
                              + "填好后点「云端生成」会先显示模型、预计等待和大概花费，确认才提交。",
                              "壁纸管家", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            bool rootOk = true;
            if (newRoot.Length > 0)
            {
                try { Directory.CreateDirectory(newRoot); }
                catch (Exception ex)
                {
                    rootOk = false;
                    MessageBox.Show("这个目录用不了：" + ex.Message, "壁纸管家", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                if (rootOk) c.LibRoot = newRoot;
            }

            AutoStart.Apply(c.AutoStart);
            ConfigStore.Save(c);

            // ApplySettings 内部会处理换库后重扫
            app.ApplySettings(rootChanged);

            Close();
        }
    }
}
