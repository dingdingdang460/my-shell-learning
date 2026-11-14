using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WallpaperManager
{
    public class MainWindow : Window
    {
        readonly App app;
        readonly WrapPanel cards = new WrapPanel();
        readonly TextBlock statusLeft = new TextBlock();
        readonly TextBlock statusRight = new TextBlock();
        readonly TextBox searchBox = new TextBox();
        string filter = "";
        WallpaperItem currentItem;

        // 11-14 深夜：角标以前只在建卡时算一次，切换壁纸后旧卡的「▶ 播放中」
        // 会一直定格在屏幕上。现在每张卡常驻一个角标控件，状态一变只重算
        // 文字/颜色，不重建卡片、不重新解码图片（整列表刷新会闪一下）。
        readonly List<Border> stateBadges = new List<Border>();

        /// <summary>建卡时缓存下来的静态状态。这些只有 RefreshList 才会变，
        /// 高频的 RefreshBadges 不去碰磁盘，也不会重复跑画面指纹。</summary>
        class BadgeInfo
        {
            public WallpaperItem Item;
            public bool HasVideo;
            public bool Queued;
            public bool Manual;
            public bool BuddyPending;   // 11-15：登记了 WorkBuddy 待领工单，等会话确认出片
        }

        // ---- 云端任务浮条 ----
        // 主人 11-13 的要求：「点击云端生成后能看到效果：要选模型还是等待中、等多久、花多少钱」。
        // 所以浮条多一个状态：3 = 下单前确认（选模型 + 看费用/等待），确认后才是 0 = 等待中。
        Border cloudToast;
        TextBlock cloudTitle, cloudName, cloudStatus, cloudSub, cloudEstimate;
        ComboBox cloudModelBox;
        Button cloudMain, cloudSettings, cloudJobs;
        System.Windows.Threading.DispatcherTimer cloudTimer;
        int cloudMode;              // 0=等待中 1=已到货 2=失败 3=下单前确认/选模型
        int cloudSpin, cloudHold;
        string cloudId = "";
        bool cloudReady;            // 有没有配好服务商 + Key
        GenApi.Provider cloudProvider;
        string cloudResolution = "720P";
        int cloudSeconds = 3;
        string cloudInfoLine = "";  // 下单时记下「模型 / 分辨率 / 花费」，到货失败时继续显示

        // 本地库筛选（只在看本地资源时出现）
        Border filterBar;
        readonly List<Border> typeChips = new List<Border>();
        string typeFilter = "all";      // all / video / static / motion


        // 在线搜索状态
        List<OnlineItem> onlineRows = new List<OnlineItem>();
        List<BiliItem> biliRows = new List<BiliItem>();
        List<BiliArticle> artRows = new List<BiliArticle>();
        readonly List<string> downloading = new List<string>();
        bool showingOnline;
        bool onlineBusy;
        string onlineKeyword = "";
        string onlineNote = "";
        string source = "web";          // web = motionbgs 壁纸站，biliart = B 站专栏图集，bili = B 站视频

        // 专栏图集预览状态（翻页用）
        BiliArticle artCurrent;
        List<string> artImages;
        int artIndex;
        bool galleryOpen;
        int loadToken;           // 每次读图集自增，关预览后旧请求的结果直接丢弃

        // 放大预览层
        Grid overlay;
        Border backBar;          // 在线结果的返回条
        TextBlock backBarText;
        // 顶栏只分「本地资源 / 在线资源」；静态图与动态视频的切换放到在线结果条里
        bool onlineMode;
        string onlineKind = "image";        // image = 静态图，video = 动态视频
        Border chipLocal;
        Border chipOnline;
        TextBlock chipLocalText;
        TextBlock chipOnlineText;
        Border kindImage;
        Border kindVideo;
        TextBlock kindImageText;
        TextBlock kindVideoText;

        // 标题中文化：记录“原文 -> 待更新的文字块”，译好后原地替换，不重建卡片
        readonly Dictionary<string, TextBlock> titleLabels =
            new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);

        static readonly Brush BgWindow = new SolidColorBrush(Color.FromRgb(16, 17, 21));
        static readonly Brush BgCard = new SolidColorBrush(Color.FromRgb(26, 27, 33));
        static readonly Brush BgCardHover = new SolidColorBrush(Color.FromRgb(33, 35, 43));
        static readonly Brush BorderIdle = new SolidColorBrush(Color.FromRgb(42, 44, 52));
        static readonly Brush BorderHover = new SolidColorBrush(Color.FromRgb(84, 96, 128));
        static readonly Brush BorderActive = new SolidColorBrush(Color.FromRgb(96, 150, 255));
        static readonly Brush TextMain = new SolidColorBrush(Color.FromRgb(232, 234, 240));
        static readonly Brush TextDim = new SolidColorBrush(Color.FromRgb(132, 138, 152));
        static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(120, 160, 255));

        public MainWindow(App app)
        {
            this.app = app;

            Title = "壁纸管家";
            Width = 1200;
            Height = 800;
            MinWidth = 900;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = BgWindow;
            Foreground = TextMain;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            UseLayoutRounding = true;

            BuildUi();
            Closing += OnClosing;
            EnableDropImport();
            SourceInitialized += delegate { RegisterHotkeys(); };
            Closing += delegate { UnregisterHotkeys(); };
            // Esc：先关大图预览，没有预览就退出网上结果回到本地图库，再没有就清空搜索词
            // ←/→：图集预览时翻上一张 / 下一张
            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                bool previewOpen = overlay != null && overlay.Visibility == Visibility.Visible;

                // F5：回滚上一张（连续按就一路往回退）
                if (e.Key == Key.F5)
                {
                    app.BackToPrevious();
                    e.Handled = true;
                    return;
                }

                if (previewOpen && galleryOpen && artCurrent != null)
                {
                    if (e.Key == Key.Left)
                    {
                        ShowArticleGallery(artCurrent, artIndex - 1);
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.Right)
                    {
                        ShowArticleGallery(artCurrent, artIndex + 1);
                        e.Handled = true;
                        return;
                    }
                }

                if (e.Key != Key.Escape) return;

                if (previewOpen)
                {
                    HidePreview();
                    e.Handled = true;
                }
                else if (showingOnline)
                {
                    GoLocal();
                    e.Handled = true;
                }
                else if (searchBox != null && searchBox.Text.Length > 0)
                {
                    searchBox.Text = "";
                    e.Handled = true;
                }
            };
            RefreshList();
        }

        void BuildUi()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(76) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 在线结果的返回条
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 本地筛选条
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });

            // ---------- 顶栏 ----------
            Grid header = new Grid();
            header.Margin = new Thickness(22, 16, 22, 10);
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel titleBox = new StackPanel();
            titleBox.VerticalAlignment = VerticalAlignment.Center;
            TextBlock title = new TextBlock();
            title.Text = "壁纸管家";
            title.FontSize = 21;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = TextMain;
            TextBlock sub = new TextBlock();
            sub.Text = "本地图库 · 一键换壁纸 · 拖图进来即导入";
            sub.FontSize = 12;
            sub.Margin = new Thickness(0, 3, 0, 0);
            sub.Foreground = TextDim;
            titleBox.Children.Add(title);
            titleBox.Children.Add(sub);
            header.Children.Add(titleBox);

            // 搜索框 + 空态提示文字（WPF 4.8 没有 Placeholder，用一层透明 TextBlock 叠上去）
            Grid searchWrap = new Grid();
            searchWrap.Width = 240;
            searchWrap.Height = 34;
            searchWrap.VerticalAlignment = VerticalAlignment.Center;
            searchWrap.HorizontalAlignment = HorizontalAlignment.Right;
            searchWrap.Margin = new Thickness(0, 0, 16, 0);

            searchBox.VerticalAlignment = VerticalAlignment.Stretch;
            searchBox.Padding = new Thickness(10, 6, 10, 6);
            searchBox.FontSize = 13;
            searchBox.Foreground = TextMain;
            searchBox.CaretBrush = TextMain;
            searchBox.Background = new SolidColorBrush(Color.FromRgb(30, 31, 38));
            searchBox.BorderBrush = BorderIdle;
            searchBox.BorderThickness = new Thickness(1);
            searchBox.ToolTip = "输入标题或分类筛选，例如 city / ocean / 城市";

            TextBlock hint = new TextBlock();
            hint.Text = "搜本地库 · 回车联网搜";
            hint.FontSize = 12;
            hint.Foreground = new SolidColorBrush(Color.FromRgb(104, 110, 124));
            hint.Margin = new Thickness(12, 0, 0, 0);
            hint.VerticalAlignment = VerticalAlignment.Center;
            hint.IsHitTestVisible = false;

            searchWrap.Children.Add(searchBox);
            searchWrap.Children.Add(hint);

            hint.Visibility = Visibility.Visible;
            searchBox.TextChanged += delegate
            {
                hint.Visibility = searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                filter = searchBox.Text;
                showingOnline = false;   // 一改关键词就回到本地结果

                // 清空是全量操作（回本地图库、下载入库后清词都走这条），必须立刻刷；
                // 打字则防抖 —— 每敲一个字就重建全部卡片，库大时会明显拖手。
                if (searchBox.Text.Length == 0) RefreshList();
                else ScheduleFilterRefresh();
            };
            searchBox.GotKeyboardFocus += delegate
            {
                if (searchBox.Text.Length == 0) hint.Visibility = Visibility.Collapsed;
            };
            searchBox.LostKeyboardFocus += delegate
            {
                if (searchBox.Text.Length == 0) hint.Visibility = Visibility.Visible;
            };

            // 回车 = 到当前图源联网搜；不回车就是纯本地过滤，两种都顺手
            searchBox.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    StartOnlineSearch(searchBox.Text);
                }
            };

            // 顶栏只留两个：本地资源 / 在线资源。
            // 原来这里是「壁纸站 / B站图集 / B站视频」三个，把技术细节摊在了用户面前；
            // 现在在线具体搜哪儿由程序按关键词自己决定，想换类型到结果条上切。
            chipLocal = MakeSourceChip("本地资源", "local", out chipLocalText);
            chipOnline = MakeSourceChip("在线资源", "online", out chipOnlineText);

            StackPanel rightTop = new StackPanel();
            rightTop.Orientation = Orientation.Horizontal;
            rightTop.HorizontalAlignment = HorizontalAlignment.Right;
            rightTop.VerticalAlignment = VerticalAlignment.Center;

            rightTop.Children.Add(chipLocal);
            rightTop.Children.Add(chipOnline);
            rightTop.Children.Add(searchWrap);
            Grid.SetColumn(rightTop, 1);
            header.Children.Add(rightTop);
            UpdateChips();

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.VerticalAlignment = VerticalAlignment.Center;

            // 飘动视频：最显眼的入口。之前只有「卡片右键」和「大图预览的第三个按钮」，
            // 而卡片单击是直接设为壁纸、根本进不到预览面板，等于藏起来了。
            // 这里给它一个常驻按钮，作用于当前选中的那张图：有片就播，没片就下单。
            Button btnCloud = MakeButton("✦ 飘动视频");
            btnCloud.ToolTip = "给当前这张图生成一段 3 秒「头发 / 衣服自然飘」的短片。\n"
                             + "已经有片子就直接播；还没有就下一单，生成好会自动换上，不用守着。\n"
                             + "想换一片：右键 →「重新生成一片」，或看大图时点第三个按钮。\n"
                             + "没配好服务商 / API Key 会弹窗说清缺什么，不会假装在生成。";
            btnCloud.Background = new SolidColorBrush(Color.FromRgb(56, 46, 82));
            btnCloud.BorderBrush = new SolidColorBrush(Color.FromRgb(138, 112, 196));
            btnCloud.Foreground = new SolidColorBrush(Color.FromRgb(228, 217, 255));
            btnCloud.Click += delegate { CloudForCurrent(); };

            Button btnNext = MakeButton("换一张");
            btnNext.Click += delegate { app.RotateNext(); };
            Button btnGreet = MakeButton("预览欢迎");
            btnGreet.Click += delegate { app.ShowGreeting(); };
            Button btnReload = MakeButton("刷新库");
            btnReload.Click += delegate
            {
                app.ReloadLibrary();
                RefreshList();
                SetStatus("已重新扫描图库");
            };
            Button btnSettings = MakeButton("设置");
            btnSettings.Click += delegate { app.OpenSettings(); };

            buttons.Children.Add(btnCloud);
            buttons.Children.Add(btnNext);
            buttons.Children.Add(btnGreet);
            buttons.Children.Add(btnReload);
            buttons.Children.Add(btnSettings);
            Grid.SetColumn(buttons, 2);
            header.Children.Add(buttons);

            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // ---------- 在线结果返回条：只要在看网上的结果就顶在这里，随时能退回本地图库 ----------
            backBar = new Border();
            backBar.Background = new SolidColorBrush(Color.FromRgb(25, 39, 61));
            backBar.BorderBrush = new SolidColorBrush(Color.FromRgb(41, 66, 100));
            backBar.BorderThickness = new Thickness(1);
            backBar.CornerRadius = new CornerRadius(8);
            backBar.Margin = new Thickness(16, 0, 16, 10);
            backBar.Padding = new Thickness(12, 7, 14, 7);
            backBar.Visibility = Visibility.Collapsed;

            DockPanel backRow = new DockPanel();
            backRow.LastChildFill = true;

            Button btnBack = MakeButton("← 返回本地图库");
            btnBack.Margin = new Thickness(0, 0, 12, 0);
            btnBack.Padding = new Thickness(12, 4, 12, 4);
            btnBack.FontSize = 12.5;
            btnBack.VerticalAlignment = VerticalAlignment.Center;
            btnBack.Background = new SolidColorBrush(Color.FromRgb(46, 92, 168));
            btnBack.BorderBrush = new SolidColorBrush(Color.FromRgb(64, 118, 208));
            btnBack.Click += delegate { GoLocal(); };
            DockPanel.SetDock(btnBack, Dock.Left);
            backRow.Children.Add(btnBack);

            backBarText = new TextBlock();
            backBarText.FontSize = 12.5;
            backBarText.Foreground = new SolidColorBrush(Color.FromRgb(184, 203, 227));
            backBarText.VerticalAlignment = VerticalAlignment.Center;
            backBarText.TextTrimming = TextTrimming.CharacterEllipsis;

            // 在线模式下才出现：静态图 / 动态视频。
            // DockPanel 先加 Right 的靠外，所以先加"动态视频"、再加"静态图"，
            // 屏幕上就是 [… 文案][静态图][动态视频]。
            kindVideo = MakeKindChip("动态视频", "video", out kindVideoText);
            kindImage = MakeKindChip("静态图", "image", out kindImageText);
            DockPanel.SetDock(kindVideo, Dock.Right);
            DockPanel.SetDock(kindImage, Dock.Right);
            backRow.Children.Add(kindVideo);
            backRow.Children.Add(kindImage);

            backRow.Children.Add(backBarText);

            backBar.Child = backRow;
            Grid.SetRow(backBar, 1);
            root.Children.Add(backBar);

            // ---------- 本地筛选条 ----------
            filterBar = BuildFilterBar();
            Grid.SetRow(filterBar, 2);
            root.Children.Add(filterBar);

            // ---------- 卡片区 ----------
            cards.Margin = new Thickness(16, 0, 16, 10);
            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.Content = cards;
            scroll.Padding = new Thickness(0, 0, 6, 0);
            Grid.SetRow(scroll, 3);
            root.Children.Add(scroll);

            // ---------- 状态栏 ----------
            Border bar = new Border();
            bar.Background = new SolidColorBrush(Color.FromRgb(20, 21, 26));
            bar.BorderBrush = new SolidColorBrush(Color.FromRgb(32, 33, 40));
            bar.BorderThickness = new Thickness(0, 1, 0, 0);

            DockPanel barPanel = new DockPanel();
            barPanel.Margin = new Thickness(20, 0, 20, 0);
            barPanel.LastChildFill = true;

            statusRight.FontSize = 12;
            statusRight.Foreground = TextDim;
            statusRight.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(statusRight, Dock.Right);

            statusLeft.FontSize = 12;
            statusLeft.Foreground = TextDim;
            statusLeft.VerticalAlignment = VerticalAlignment.Center;

            barPanel.Children.Add(statusRight);
            barPanel.Children.Add(statusLeft);
            bar.Child = barPanel;
            Grid.SetRow(bar, 4);
            root.Children.Add(bar);

            // ---------- 云端任务浮条：有单才出现，排队中每秒报一次秒数 ----------
            StackPanel cbox = new StackPanel();

            DockPanel chead = new DockPanel();
            chead.LastChildFill = true;

            cloudTitle = new TextBlock();
            cloudTitle.Text = "☁ 云端生成";
            cloudTitle.FontSize = 12.5;
            cloudTitle.FontWeight = FontWeights.SemiBold;
            cloudTitle.Foreground = new SolidColorBrush(Color.FromRgb(228, 217, 255));
            cloudTitle.VerticalAlignment = VerticalAlignment.Center;

            Button cclose = new Button();
            cclose.Content = "✕";
            cclose.FontSize = 11;
            cclose.Padding = new Thickness(6, 0, 6, 0);
            cclose.Background = Brushes.Transparent;
            cclose.Foreground = TextDim;
            cclose.BorderThickness = new Thickness(0);
            cclose.Cursor = Cursors.Hand;
            cclose.ToolTip = "收起（任务还在后台跑，到货会自动换上）";
            cclose.Click += delegate { HideCloudToast(); };
            DockPanel.SetDock(cclose, Dock.Right);
            chead.Children.Add(cclose);
            chead.Children.Add(cloudTitle);
            cbox.Children.Add(chead);

            cloudName = new TextBlock();
            cloudName.FontSize = 12.5;
            cloudName.Foreground = TextMain;
            cloudName.TextTrimming = TextTrimming.CharacterEllipsis;
            cloudName.Margin = new Thickness(0, 7, 0, 0);
            cbox.Children.Add(cloudName);

            // 下单前：模型下拉（每一项都带单价 / 预计等待 / 3 秒总价）
            cloudModelBox = new ComboBox();
            cloudModelBox.FontSize = 11.5;
            cloudModelBox.Margin = new Thickness(0, 9, 0, 0);
            cloudModelBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            cloudModelBox.MaxDropDownHeight = 220;
            cloudModelBox.Visibility = Visibility.Collapsed;
            cloudModelBox.SelectionChanged += delegate { UpdateCloudChoiceEstimate(); };
            cbox.Children.Add(cloudModelBox);

            // 下单前 / 等待中都显示：大概花费 + 预计等待
            cloudEstimate = new TextBlock();
            cloudEstimate.FontSize = 11.5;
            cloudEstimate.FontWeight = FontWeights.SemiBold;
            cloudEstimate.Foreground = new SolidColorBrush(Color.FromRgb(150, 214, 176));
            cloudEstimate.TextWrapping = TextWrapping.Wrap;
            cloudEstimate.LineHeight = 18;
            cloudEstimate.Margin = new Thickness(0, 8, 0, 0);
            cbox.Children.Add(cloudEstimate);

            cloudStatus = new TextBlock();
            cloudStatus.FontSize = 12;
            cloudStatus.Foreground = new SolidColorBrush(Color.FromRgb(206, 196, 240));
            cloudStatus.Margin = new Thickness(0, 4, 0, 0);
            cbox.Children.Add(cloudStatus);

            cloudSub = new TextBlock();
            cloudSub.FontSize = 10.5;
            cloudSub.Foreground = TextDim;
            cloudSub.TextWrapping = TextWrapping.Wrap;
            cloudSub.Margin = new Thickness(0, 5, 0, 0);
            cbox.Children.Add(cloudSub);

            StackPanel cbtns = new StackPanel();
            cbtns.Orientation = Orientation.Horizontal;
            cbtns.Margin = new Thickness(0, 9, 0, 0);

            cloudMain = MakeButton("取消这单");
            cloudMain.Padding = new Thickness(11, 4, 11, 4);
            cloudMain.FontSize = 12;
            cloudMain.Focusable = false;      // 别让 WPF 焦点样式把按钮染成浅蓝底白字
            cloudMain.Background = new SolidColorBrush(Color.FromRgb(36, 38, 46));
            cloudMain.Click += OnCloudMainClick;

            cloudSettings = MakeButton("怎么生成？");
            cloudSettings.Padding = new Thickness(11, 4, 11, 4);
            cloudSettings.FontSize = 12;
            cloudSettings.Focusable = false;
            cloudSettings.Background = new SolidColorBrush(Color.FromRgb(36, 38, 46));
            cloudSettings.ToolTip = "打开设置，切换服务商 / 模型 / 分辨率并填 API Key"
                                 + "；工单是纯文本 JSON，放在 " + CloudGen.QueueDir;
            cloudSettings.Click += delegate
            {
                if (cloudMode == 3)      // 确认态：这个按钮是「取消」
                {
                    HideCloudToast();
                    if (cloudTimer != null) cloudTimer.Stop();
                    return;
                }
                HideCloudToast();
                if (app == null) return;
                SetStatus("生成通道：" + app.CloudChannel());
                app.OpenSettings();
            };

            // 11-14 主人问「我下单在哪能看到？」：以前工单只有磁盘上的 JSON，界面完全看不见。
            // 给一个按钮直接打开目录并选中当前这单，纯文本，主人自己能看、能改、能删。
            cloudJobs = MakeButton("查看工单");
            cloudJobs.Padding = new Thickness(11, 4, 11, 4);
            cloudJobs.FontSize = 12;
            cloudJobs.Focusable = false;
            cloudJobs.Background = new SolidColorBrush(Color.FromRgb(36, 38, 46));
            cloudJobs.ToolTip = "打开工单目录并选中这张单；工单是纯文本 JSON，可自己看 / 改 / 删";
            cloudJobs.Visibility = Visibility.Collapsed;
            cloudJobs.Click += delegate { if (app != null) app.OpenJobFolder(cloudId); };

            cbtns.Children.Add(cloudMain);
            cbtns.Children.Add(cloudSettings);
            cbtns.Children.Add(cloudJobs);
            cbox.Children.Add(cbtns);

            cloudToast = new Border();
            cloudToast.Child = cbox;
            cloudToast.Width = 402;
            cloudToast.CornerRadius = new CornerRadius(10);
            cloudToast.Padding = new Thickness(13, 10, 13, 11);
            // 不透明：半透明会让背后的卡片透上来，看着像文字叠在图上
            cloudToast.Background = new SolidColorBrush(Color.FromRgb(24, 25, 32));
            cloudToast.BorderBrush = new SolidColorBrush(Color.FromRgb(138, 112, 196));
            cloudToast.BorderThickness = new Thickness(1);
            cloudToast.HorizontalAlignment = HorizontalAlignment.Right;
            cloudToast.VerticalAlignment = VerticalAlignment.Bottom;
            cloudToast.Margin = new Thickness(0, 0, 18, 46);
            cloudToast.Visibility = Visibility.Collapsed;
            cloudToast.ToolTip = "任务在后台跑，点 ✕ 收起不影响出片";
            Grid.SetRow(cloudToast, 1);
            Grid.SetRowSpan(cloudToast, 4);
            root.Children.Add(cloudToast);

            // ---------- 放大预览层（覆盖整个窗口，默认隐藏） ----------
            overlay = new Grid();
            overlay.Background = new SolidColorBrush(Color.FromArgb(236, 8, 9, 12));
            overlay.Visibility = Visibility.Collapsed;
            overlay.MouseLeftButtonUp += delegate { HidePreview(); };
            Grid.SetRow(overlay, 0);
            Grid.SetRowSpan(overlay, 5);
            root.Children.Add(overlay);

            Content = root;
        }

        static Button MakeButton(string text)
        {
            Button b = new Button();
            b.Content = text;
            b.FontSize = 13;
            b.Padding = new Thickness(14, 6, 14, 6);
            b.Margin = new Thickness(6, 0, 0, 0);
            b.Background = new SolidColorBrush(Color.FromRgb(36, 38, 46));
            b.Foreground = TextMain;
            b.BorderBrush = BorderIdle;
            b.BorderThickness = new Thickness(1);
            b.Cursor = Cursors.Hand;
            return b;
        }

        Border MakeSourceChip(string text, string value, out TextBlock label)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(15);
            b.Padding = new Thickness(12, 6, 12, 6);
            b.Margin = new Thickness(8, 0, 0, 0);
            b.BorderThickness = new Thickness(1);
            b.Cursor = Cursors.Hand;
            b.VerticalAlignment = VerticalAlignment.Center;
            if (value == "local")
                b.ToolTip = "只看自己库里的壁纸（可以搜索、换、删）";
            else
                b.ToolTip = "联网找新壁纸：中文搜图集，英文搜壁纸站";

            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 12.5;
            b.Child = t;
            label = t;

            string captured = value;
            b.MouseLeftButtonUp += delegate { SetMode(captured); };
            return b;
        }

        /// <summary>在线结果条里的小切换：静态图 / 动态视频</summary>
        Border MakeKindChip(string text, string value, out TextBlock label)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(11);
            b.Padding = new Thickness(10, 3, 10, 3);
            b.Margin = new Thickness(8, 0, 0, 0);
            b.BorderThickness = new Thickness(1);
            b.Cursor = Cursors.Hand;
            b.VerticalAlignment = VerticalAlignment.Center;

            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 12;
            b.Child = t;
            label = t;

            string captured = value;
            b.MouseLeftButtonUp += delegate { SetKind(captured); };
            return b;
        }

        void UpdateChips()
        {
            StyleChip(chipLocal, chipLocalText, !onlineMode);
            StyleChip(chipOnline, chipOnlineText, onlineMode);

            bool on = showingOnline;
            if (kindImage != null)
            {
                kindImage.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                kindVideo.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                StyleChip(kindImage, kindImageText, onlineKind == "image");
                StyleChip(kindVideo, kindVideoText, onlineKind == "video");
            }
        }

        System.Windows.Threading.DispatcherTimer filterDebounce;

        /// <summary>输入防抖：停手 120ms 才重建列表，期间继续输入就重新计时。</summary>
        void ScheduleFilterRefresh()
        {
            if (filterDebounce == null)
            {
                filterDebounce = new System.Windows.Threading.DispatcherTimer();
                filterDebounce.Interval = TimeSpan.FromMilliseconds(120);
                filterDebounce.Tick += delegate
                {
                    filterDebounce.Stop();
                    RefreshList();
                };
            }
            filterDebounce.Stop();
            filterDebounce.Start();
        }

        /// <summary>顶栏切换：本地资源 / 在线资源</summary>
        void SetMode(string m)
        {
            Log.Note("顶栏切换：" + m);
            bool toOnline = (m == "online");
            if (!toOnline)
            {
                onlineMode = false;
                GoLocal();
                UpdateChips();
                SetStatus("已回到本地资源（点卡片换壁纸，卡片右上角可删除）");
                return;
            }

            onlineMode = true;
            UpdateChips();

            string kw = searchBox.Text == null ? "" : searchBox.Text.Trim();
            if (kw.Length == 0)
            {
                SetStatus("已切到在线资源：输入关键词后回车（中文搜图集，英文搜壁纸站；结果条上可切动态视频）");
                return;
            }
            StartOnlineSearch(kw);
        }

        /// <summary>在线结果里切换「静态图 / 动态视频」</summary>
        void SetKind(string k)
        {
            Log.Note("在线类型切换：" + k);
            if (onlineKind == k) return;
            onlineKind = k;
            UpdateChips();
            if (showingOnline) StartOnlineSearch(onlineKeyword);
        }

        static void StyleChip(Border b, TextBlock t, bool active)
        {
            if (b == null || t == null) return;
            if (active)
            {
                b.Background = new SolidColorBrush(Color.FromRgb(46, 92, 168));
                b.BorderBrush = new SolidColorBrush(Color.FromRgb(64, 118, 208));
                t.Foreground = Brushes.White;
            }
            else
            {
                b.Background = new SolidColorBrush(Color.FromRgb(30, 31, 38));
                b.BorderBrush = BorderIdle;
                t.Foreground = TextDim;
            }
        }

        // ---------------- 本地筛选条 ----------------

        /// <summary>内部用的小委托：三个筛选组共用一套建芯片的逻辑</summary>
        delegate void PickHandler(string value);

        Border BuildFilterBar()
        {
            Border bar = new Border();
            bar.Margin = new Thickness(16, 0, 16, 8);
            bar.Padding = new Thickness(10, 6, 10, 6);
            bar.CornerRadius = new CornerRadius(8);
            bar.Background = new SolidColorBrush(Color.FromRgb(22, 23, 28));
            bar.BorderBrush = new SolidColorBrush(Color.FromRgb(36, 38, 46));
            bar.BorderThickness = new Thickness(1);

            WrapPanel row = new WrapPanel();
            row.VerticalAlignment = VerticalAlignment.Center;

            AddFilterGroup(row, "类型",
                new string[] { "all", "video", "static", "motion" },
                new string[] { "全部", "视频", "静态", "有飘动视频" },
                typeChips, OnTypePick);

            bar.Child = row;
            StyleFilterChips();
            return bar;
        }

        void AddFilterGroup(WrapPanel row, string title, string[] values, string[] texts,
                            List<Border> store, PickHandler onPick)
        {
            TextBlock lab = new TextBlock();
            lab.Text = title;
            lab.FontSize = 11.5;
            lab.Foreground = new SolidColorBrush(Color.FromRgb(104, 110, 124));
            lab.VerticalAlignment = VerticalAlignment.Center;
            lab.Margin = new Thickness(row.Children.Count == 0 ? 0 : 16, 0, 4, 0);
            row.Children.Add(lab);

            for (int i = 0; i < values.Length; i++)
            {
                string v = values[i];
                Border chip = MakeFilterChip(texts[i], v);
                chip.MouseLeftButtonUp += delegate { onPick(v); };
                store.Add(chip);
                row.Children.Add(chip);
            }
        }

        Border MakeFilterChip(string text, string value)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(10);
            b.Padding = new Thickness(9, 2, 9, 2);
            b.Margin = new Thickness(3, 2, 3, 2);
            b.BorderThickness = new Thickness(1);
            b.Cursor = Cursors.Hand;
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Tag = value;

            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 11.5;
            b.Child = t;
            return b;
        }

        void OnTypePick(string v) { typeFilter = v; StyleFilterChips(); RefreshList(); }

        /// <summary>自检用：按名字切类型筛选（用来核对角标和空态），和点芯片是同一条路</summary>
        public void SetTypeFilter(string v) { OnTypePick(v); }

        /// <summary>
        /// 自检用：往搜索框里塞关键词。故意走 searchBox.Text 而不是直接改 filter，
        /// 这样连「TextChanged → 防抖 → RefreshList」整条链路一起验到。
        /// </summary>
        public void SetSearchForTest(string text) { searchBox.Text = text == null ? "" : text; }

        void StyleFilterChips()
        {
            StyleFilterSet(typeChips, typeFilter);
        }

        static void StyleFilterSet(List<Border> chips, string active)
        {
            for (int i = 0; i < chips.Count; i++)
            {
                Border b = chips[i];
                bool on = string.Equals(b.Tag as string, active, StringComparison.Ordinal);
                b.Background = on ? new SolidColorBrush(Color.FromRgb(46, 92, 168))
                                  : new SolidColorBrush(Color.FromRgb(30, 31, 38));
                b.BorderBrush = on ? new SolidColorBrush(Color.FromRgb(64, 118, 208)) : BorderIdle;
                TextBlock t = b.Child as TextBlock;
                if (t != null) t.Foreground = on ? Brushes.White : TextDim;
            }
        }

        public void RefreshList()
        {
            cards.Children.Clear();
            titleLabels.Clear();
            stateBadges.Clear();        // 旧角标随旧卡片一起作废，别留悬空引用

            UpdateBackBar();
            UpdateChips();
            if (showingOnline) RenderOnline();
            else RenderLocal();

            UpdateStatusRight();
            if (!showingOnline) HighlightCurrent();
            RefreshBadges();            // 全量重建后按真实状态把角标点亮
        }

        /// <summary>在线结果返回条的显隐与文案：只要在看网上的东西就亮出来</summary>
        void UpdateBackBar()
        {
            // 筛选条只在看本地库时有意义：在线结果里它既筛不到东西，还白占一行
            if (filterBar != null)
                filterBar.Visibility = showingOnline ? Visibility.Collapsed : Visibility.Visible;

            if (backBar == null) return;
            if (!showingOnline)
            {
                backBar.Visibility = Visibility.Collapsed;
                return;
            }

            backBar.Visibility = Visibility.Visible;
            string srcName = source == "bili" ? "在线动态视频"
                           : (source == "biliart" ? "在线图集" : "在线壁纸站");
            string kw = onlineKeyword == null ? "" : onlineKeyword;
            backBarText.Text = onlineBusy
                ? "正在从" + srcName + "找「" + kw + "」…　想看自己存的壁纸，点左边按钮或按 Esc"
                : "正在看" + srcName + "的搜索结果「" + kw + "」· 想看自己存的壁纸，点左边按钮或按 Esc";
        }

        /// <summary>从网上结果退回本地图库：清掉在线状态和筛选词，回到全部本地壁纸</summary>
        void GoLocal()
        {
            showingOnline = false;
            onlineMode = false;
            onlineBusy = false;
            onlineKeyword = "";
            onlineNote = "";
            filter = "";

            if (searchBox.Text.Length > 0)
                searchBox.Text = "";     // 触发 TextChanged，顺带刷新列表
            else
                RefreshList();

            SetStatus("已回到本地图库 · 共 " + app.Items.Count + " 张，点卡片即可设为壁纸");
        }

        // ---------------- 本地结果 ----------------
        void RenderLocal()
        {
            string q = (filter == null ? "" : filter.Trim());
            List<WallpaperItem> rows = FilterLocal(q);

            for (int i = 0; i < rows.Count; i++) cards.Children.Add(BuildCard(rows[i]));

            int videos = 0, statics = 0, motions = 0;
            for (int i = 0; i < app.Items.Count; i++)
            {
                WallpaperItem x = app.Items[i];
                if (x.IsVideo) videos++; else statics++;
                if (app.HasCloudVideo(x)) motions++;
            }

            if (app.Items.Count == 0)
            {
                cards.Children.Add(MakeHint("图库是空的。请确认 " + app.Config.LibRoot + " 存在，或点「刷新库」重新扫描。"));
            }
            else if (rows.Count == 0)
            {
                if (q.Length > 0) cards.Children.Add(BuildNoMatch(q));
                else cards.Children.Add(MakeHint("当前筛选下没有壁纸：把「类型」切回「全部」试试。"));
            }

            string mark = FilterMark();
            SetStatus(string.Format("库内 {0} 张（视频 {1} · 静态 {2} · 有飘动片 {3}），当前显示 {4} 张{5}",
                app.Items.Count, videos, statics, motions, rows.Count, mark));

            List<string> allTitles = new List<string>();
            for (int i = 0; i < app.Items.Count; i++) allTitles.Add(app.Items[i].Title);
            if (allTitles.Count > 0) PrefetchTitles(allTitles);
        }

        /// <summary>当前筛选条件的中文摘要，只在真的筛过之后才显示</summary>
        string FilterMark()
        {
            List<string> parts = new List<string>();
            if (typeFilter == "video") parts.Add("视频");
            else if (typeFilter == "static") parts.Add("静态");
            else if (typeFilter == "motion") parts.Add("有飘动视频");
            if (parts.Count == 0) return "";

            StringBuilder sb = new StringBuilder("　· 已筛选：");
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) sb.Append(" / ");
                sb.Append(parts[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 本地库的「筛选 + 搜索」唯一实现。顺序就是 index.json 的顺序（按入库先后）。
        /// 搜索同时吃三种写法：标题原文（含译名）、分类中文名、拼音首字母（cs → 城市）。
        /// </summary>
        List<WallpaperItem> FilterLocal(string q)
        {
            List<WallpaperItem> rows = new List<WallpaperItem>();

            for (int i = 0; i < app.Items.Count; i++)
            {
                WallpaperItem it = app.Items[i];

                if (typeFilter == "video" && !it.IsVideo) continue;
                if (typeFilter == "static" && it.IsVideo) continue;
                // 「有飘动视频」= 静态图配好了云端短片。视频本身就是动态的，不该混进这一档。
                if (typeFilter == "motion" && !app.HasCloudVideo(it)) continue;

                if (q.Length > 0)
                {
                    string tagKey = TagNames.SearchKey(it.Tag);
                    if (!Pinyin.Matches(it.Title, tagKey, q)
                        && !Pinyin.Matches(DisplayTitle(it.Title), tagKey, q)) continue;
                }

                rows.Add(it);
            }

            return rows;
        }

        /// <summary>
        /// 自检：搜索 / 筛选。跑的是界面真正在用的 FilterLocal，
        /// 不是另写一套「测试专用逻辑」，否则测过了界面上仍然可能不好使。
        /// 每行一条结论，最后一行 PASS / FAIL，方便直接 grep。
        /// </summary>
        public string SelfTestSearch()
        {
            StringBuilder sb = new StringBuilder();
            bool ok = true;

            // 1) 拼音首字母：这两个词覆盖了「单字声母」和「连续多字」
            string a = Pinyin.Initials("城市夜景");
            string b = Pinyin.Initials("动漫壁纸");
            bool pinyinOk = (a == "CSYJ") && (b == "DMBZ");
            if (!pinyinOk) ok = false;
            sb.Append("自检 --searchtest：拼音 城市夜景→").Append(a)
              .Append("  动漫壁纸→").Append(b)
              .Append(pinyinOk ? "  √" : "  ×（应为 CSYJ / DMBZ）").Append("\r\n");

            // 2) 拿库里真有的一条中文标题，用它自己的首字母去搜，必须能搜回自己
            WallpaperItem sample = null;
            string sampleIni = "";
            for (int i = 0; i < app.Items.Count; i++)
            {
                string ini = Pinyin.Initials(app.Items[i].Title);
                if (ini.Length >= 2) { sample = app.Items[i]; sampleIni = ini; break; }
            }
            if (sample != null)
            {
                List<WallpaperItem> hit = FilterLocal(sampleIni.Substring(0, 2));
                bool found = false;
                for (int i = 0; i < hit.Count; i++) if (hit[i].Id == sample.Id) found = true;
                if (!found) ok = false;
                sb.Append("自检 --searchtest：首字母搜「").Append(sampleIni.Substring(0, 2))
                  .Append("」命中 ").Append(hit.Count).Append(" 张，含样本 ")
                  .Append(found ? "√" : "×").Append("（").Append(sample.Title).Append("）\r\n");
            }
            else
            {
                sb.Append("自检 --searchtest：库里没有中文标题，跳过首字母搜索断言\r\n");
            }

            // 3) 分类首字母：库里标题都是英文，敲 cs 应该命中「城市」这一整类
            List<WallpaperItem> cityHit = FilterLocal("cs");
            int wantCity = 0;
            for (int i = 0; i < app.Items.Count; i++)
                if (string.Equals(app.Items[i].Tag, "city", StringComparison.OrdinalIgnoreCase)) wantCity++;
            bool cityOk = wantCity == 0 || cityHit.Count >= wantCity;
            if (!cityOk) ok = false;
            sb.Append("自检 --searchtest：分类首字母「cs」命中 ").Append(cityHit.Count)
              .Append(" 张（城市类共 ").Append(wantCity).Append(" 张")
              .Append(cityHit.Count > wantCity
                      ? "，多出的 " + (cityHit.Count - wantCity) + " 张是标题首字母恰好含 cs"
                      : "")
              .Append("）")
              .Append(cityOk ? "  √" : "  ×（分类首字母没生效）").Append("\r\n");

            // 4) 类型筛选：三个桶的数量必须和真实条目数一致
            string savedType = typeFilter;

            int wantVideo = 0, wantStatic = 0, wantMotion = 0;
            for (int i = 0; i < app.Items.Count; i++)
            {
                if (app.Items[i].IsVideo) wantVideo++; else wantStatic++;
                if (app.HasCloudVideo(app.Items[i])) wantMotion++;
            }

            typeFilter = "video"; int gotVideo = FilterLocal("").Count;
            typeFilter = "static"; int gotStatic = FilterLocal("").Count;
            typeFilter = "motion"; int gotMotion = FilterLocal("").Count;
            typeFilter = savedType;

            bool typeOk = (gotVideo == wantVideo) && (gotStatic == wantStatic) && (gotMotion == wantMotion);
            if (!typeOk) ok = false;
            sb.Append("自检 --searchtest：类型 视频 ").Append(gotVideo).Append("/").Append(wantVideo)
              .Append("  静态 ").Append(gotStatic).Append("/").Append(wantStatic)
              .Append("  有飘动片 ").Append(gotMotion).Append("/").Append(wantMotion)
              .Append(typeOk ? "  √" : "  ×").Append("\r\n");

            // 4) 取色：筛选条上已经不用它了，但欢迎语生成要拿色调兜底，所以照样得能算
            int cool = 0, warm = 0, dark = 0, bright = 0, unknown = 0;
            for (int i = 0; i < app.Items.Count; i++)
            {
                WallpaperItem it = app.Items[i];
                string path = !string.IsNullOrEmpty(it.ImagePath) ? it.ImagePath : it.ThumbAbs;
                string c = ColorTag.Of(path);
                if (c == ColorTag.Cool) cool++;
                else if (c == ColorTag.Warm) warm++;
                else if (c == ColorTag.Dark) dark++;
                else if (c == ColorTag.Bright) bright++;
                else unknown++;
            }
            bool colorOk = (cool + warm + dark + bright) > 0;
            if (!colorOk) ok = false;
            sb.Append("自检 --searchtest：取色（欢迎语兜底用）冷 ").Append(cool).Append(" 暖 ").Append(warm)
              .Append(" 暗 ").Append(dark).Append(" 亮 ").Append(bright)
              .Append(" 取不到 ").Append(unknown)
              .Append(colorOk ? "  √" : "  ×（一张都没算出来）").Append("\r\n");

            typeFilter = savedType;

            sb.Append("自检 --searchtest：").Append(ok ? "PASS" : "FAIL");
            RefreshList();
            return sb.ToString();
        }

        UIElement MakeHint(string text)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.Foreground = TextDim;
            t.FontSize = 13;
            t.Margin = new Thickness(10, 20, 0, 0);
            return t;
        }

        /// <summary>网上也没结果时的空态：说明 + 明确的回本地出口</summary>
        UIElement BuildOnlineEmpty(string text)
        {
            StackPanel box = new StackPanel();
            box.Margin = new Thickness(10, 22, 10, 10);
            box.HorizontalAlignment = HorizontalAlignment.Left;

            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontSize = 13;
            t.Foreground = TextDim;
            t.TextWrapping = TextWrapping.Wrap;
            t.MaxWidth = 560;
            box.Children.Add(t);

            Button b = MakeButton("← 返回本地图库");
            b.Margin = new Thickness(0, 14, 0, 0);
            b.Padding = new Thickness(16, 7, 16, 7);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Background = new SolidColorBrush(Color.FromRgb(46, 92, 168));
            b.BorderBrush = new SolidColorBrush(Color.FromRgb(64, 118, 208));
            b.Click += delegate { GoLocal(); };
            box.Children.Add(b);

            return box;
        }

        /// <summary>搜不到时的空态：说清本地有什么，并给一条去网上找的出口</summary>
        UIElement BuildNoMatch(string keyword)
        {
            StackPanel box = new StackPanel();
            box.Margin = new Thickness(12, 22, 12, 10);
            box.HorizontalAlignment = HorizontalAlignment.Left;

            TextBlock t1 = new TextBlock();
            t1.Text = "本地库里没有「" + keyword + "」这一类";
            t1.FontSize = 15;
            t1.FontWeight = FontWeights.SemiBold;
            t1.Foreground = TextMain;
            box.Children.Add(t1);

            TextBlock t2 = new TextBlock();
            t2.Text = "本地共 " + app.Items.Count + " 张，只有下面这些分类（点一下直接筛）：";
            t2.FontSize = 12.5;
            t2.Foreground = TextDim;
            t2.Margin = new Thickness(0, 8, 0, 0);
            box.Children.Add(t2);

            WrapPanel chips = new WrapPanel();
            chips.Margin = new Thickness(0, 10, 0, 0);
            List<string> tags = new List<string>();
            for (int i = 0; i < app.Items.Count; i++)
            {
                string tg = app.Items[i].Tag;
                if (string.IsNullOrEmpty(tg)) continue;
                if (!tags.Contains(tg)) tags.Add(tg);
            }
            for (int i = 0; i < tags.Count; i++)
            {
                string cn = TagNames.Cn(tags[i]);
                Border chip = new Border();
                chip.Background = new SolidColorBrush(Color.FromRgb(36, 38, 46));
                chip.BorderBrush = BorderIdle;
                chip.BorderThickness = new Thickness(1);
                chip.CornerRadius = new CornerRadius(12);
                chip.Padding = new Thickness(12, 5, 12, 5);
                chip.Margin = new Thickness(0, 0, 8, 8);
                chip.Cursor = Cursors.Hand;

                TextBlock ct = new TextBlock();
                ct.Text = cn;
                ct.FontSize = 12.5;
                ct.Foreground = TextMain;
                chip.Child = ct;

                string captured = cn;
                chip.MouseLeftButtonUp += delegate { searchBox.Text = captured; };
                chips.Children.Add(chip);
            }
            box.Children.Add(chips);

            Button btn = MakeButton("到网上找「" + keyword + "」");
            btn.Margin = new Thickness(0, 12, 0, 0);
            btn.Padding = new Thickness(18, 8, 18, 8);
            btn.HorizontalAlignment = HorizontalAlignment.Left;
            btn.Background = new SolidColorBrush(Color.FromRgb(46, 92, 168));
            btn.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 110, 196));

            string kw = keyword;
            btn.Click += delegate { StartOnlineSearch(kw); };
            box.Children.Add(btn);

            TextBlock t3 = new TextBlock();
            t3.Text = "联网搜索只拉缩略图（每张十几 KB），点卡片先看大图，确认要了才下载。中文关键词自动搜图集，英文自动搜壁纸站；动态视频在结果条右侧切换。";
            t3.FontSize = 12;
            t3.Foreground = new SolidColorBrush(Color.FromRgb(104, 110, 124));
            t3.Margin = new Thickness(0, 10, 0, 0);
            t3.TextWrapping = TextWrapping.Wrap;
            t3.MaxWidth = 520;
            box.Children.Add(t3);

            return box;
        }

        // ---------------- 在线结果 ----------------
        void StartOnlineSearch(string keyword)
        {
            keyword = (keyword == null ? "" : keyword.Trim());
            if (keyword.Length == 0)
            {
                SetStatus("先在搜索框里输入要找的内容");
                return;
            }

            onlineKeyword = keyword;
            showingOnline = true;
            onlineBusy = true;

            // 具体走哪个后端由程序自己决定，用户只需要关心"静态图 / 动态视频"：
            //   动态视频 → B 站视频（1080P，匿名可下）
            //   静态图   → 中文走 B 站专栏图集（一篇几十张原图），英文走壁纸站
            bool cn = Translate.HasChinese(keyword);
            source = onlineKind == "video" ? "bili" : (cn ? "biliart" : "web");
            UpdateChips();

            onlineRows = new List<OnlineItem>();
            biliRows = new List<BiliItem>();
            artRows = new List<BiliArticle>();
            onlineNote = "";
            RefreshList();

            // B 站专栏图集：翻 4 页拿 80 篇，每篇十几到几十张原图
            if (source == "biliart")
            {
                SetStatus("正在 B 站搜专栏图集「" + keyword + "」（翻 4 页）…");
                string kwa = keyword;
                Task.Run(delegate
                {
                    List<BiliArticle> rows = null;
                    string err = null;
                    try { rows = Bili.SearchArticles(kwa, 4); }
                    catch (Exception ex) { err = ex.Message; }

                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        onlineBusy = false;
                        if (err != null)
                        {
                            artRows = new List<BiliArticle>();
                            RefreshList();
                            SetStatus("B 站专栏搜索失败：" + err);
                            return;
                        }
                        artRows = rows;
                        RefreshList();
                    }));
                });
                return;
            }

            // B 站视频（动态壁纸）：翻 3 页，之前只取一页所以只有十几条
            if (source == "bili")
            {
                SetStatus("正在 B 站搜索「" + keyword + "」（翻 3 页）…");
                string kw = keyword;
                Task.Run(delegate
                {
                    List<BiliItem> rows = null;
                    string err = null;
                    try { rows = Bili.Search(kw, 60); }
                    catch (Exception ex) { err = ex.Message; }

                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        onlineBusy = false;
                        if (err != null)
                        {
                            biliRows = new List<BiliItem>();
                            RefreshList();
                            SetStatus("B 站搜索失败：" + err);
                            return;
                        }
                        biliRows = rows;
                        RefreshList();
                    }));
                });
                return;
            }

            // 中文分类能对上图源频道时，直接走频道页，结果比拿中文去搜准得多
            string tag = TagNames.GuessTag(keyword);
            bool byTag = tag != "online" && tag != "bili";
            SetStatus(byTag
                ? "正在从「" + TagNames.Cn(tag) + "」频道拉取…"
                : "正在联网搜索「" + keyword + "」…");

            Task.Run(delegate
            {
                List<OnlineItem> rows = null;
                string err = null;
                try
                {
                    rows = byTag ? Online.ByTag(tag, 24) : Online.Search(keyword, 24);
                }
                catch (Exception ex) { err = ex.Message; }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    onlineBusy = false;
                    if (err != null)
                    {
                        onlineRows = new List<OnlineItem>();
                        RefreshList();
                        SetStatus("联网搜索失败：" + err);
                        return;
                    }
                    onlineRows = rows;
                    if (rows.Count > 0 && !byTag && !Online.LooksRelevant(keyword, rows))
                        onlineNote = "站内没有精确匹配（该站内容是英文标题，中文词几乎搜不到），下面是相关推荐";
                    RefreshList();
                }));
            });
        }

        void RenderOnline()
        {
            if (source == "bili")
            {
                RenderBili();
                return;
            }
            if (source == "biliart")
            {
                RenderArticles();
                return;
            }

            if (onlineBusy)
            {
                cards.Children.Add(MakeHint("正在联网搜索「" + onlineKeyword + "」…"));
                SetStatus("正在联网搜索「" + onlineKeyword + "」…");
                return;
            }
            if (onlineRows.Count == 0)
            {
                cards.Children.Add(BuildOnlineEmpty("网上也没搜到「" + onlineKeyword + "」。换个词试试：cyberpunk / neon / space / anime / ocean，或者用中文搜（会自动去图集）。"));
                SetStatus("在线结果 0 条");
                return;
            }

            List<string> titles = new List<string>();
            for (int i = 0; i < onlineRows.Count; i++)
            {
                cards.Children.Add(BuildOnlineCard(onlineRows[i]));
                titles.Add(onlineRows[i].Title);
            }
            PrefetchTitles(titles);

            string note = onlineNote.Length > 0 ? "　" + onlineNote : "";
            SetStatus(string.Format("壁纸站结果 {0} 条 ·「{1}」· 点卡片看大图{2}", onlineRows.Count, onlineKeyword, note));
        }

        void RenderBili()
        {
            if (onlineBusy)
            {
                cards.Children.Add(MakeHint("正在 B 站搜索「" + onlineKeyword + "」…"));
                SetStatus("正在 B 站搜索「" + onlineKeyword + "」…");
                return;
            }
            if (biliRows.Count == 0)
            {
                cards.Children.Add(BuildOnlineEmpty("B 站没搜到「" + onlineKeyword + "」。试试「动态壁纸 4k」「动漫壁纸」「风景壁纸」。"));
                SetStatus("B 站结果 0 条");
                return;
            }

            List<string> titles = new List<string>();
            for (int i = 0; i < biliRows.Count; i++)
            {
                cards.Children.Add(BuildBiliCard(biliRows[i]));
                titles.Add(biliRows[i].Title);
            }
            PrefetchTitles(titles);

            SetStatus(string.Format("B 站视频结果 {0} 条（翻了 3 页）·「{1}」· 点卡片看大图，确认后再下载",
                biliRows.Count, onlineKeyword));
        }

        // ---------------- B 站专栏图集 ----------------

        void RenderArticles()
        {
            if (onlineBusy)
            {
                cards.Children.Add(MakeHint("正在 B 站搜专栏「" + onlineKeyword + "」，翻 4 页，稍等…"));
                SetStatus("正在 B 站搜专栏图集「" + onlineKeyword + "」…");
                return;
            }
            if (artRows.Count == 0)
            {
                cards.Children.Add(BuildOnlineEmpty("B 站没搜到「" + onlineKeyword + "」相关的图集专栏。"
                    + "试试「动漫壁纸」「唯美风景壁纸」「二次元 壁纸 4k」这类词，专栏标题一般这么写。"));
                SetStatus("B 站专栏结果 0 条");
                return;
            }

            List<string> titles = new List<string>();
            for (int i = 0; i < artRows.Count; i++)
            {
                cards.Children.Add(BuildArticleCard(artRows[i]));
                titles.Add(artRows[i].Title);
            }
            PrefetchTitles(titles);

            SetStatus(string.Format("B 站图集 {0} 篇（翻了 4 页）·「{1}」· 点一篇进去翻图，挑中的那张再下载",
                artRows.Count, onlineKeyword));
        }

        /// <summary>一篇专栏 = 一张卡；点开才能看到里面几十张原图</summary>
        Border BuildArticleCard(BiliArticle a)
        {
            Border card = new Border();
            card.Width = 244;
            card.Height = 182;
            card.Margin = new Thickness(7);
            card.CornerRadius = new CornerRadius(10);
            card.Background = BgCard;
            card.BorderBrush = BorderIdle;
            card.BorderThickness = new Thickness(1);
            card.Cursor = Cursors.Hand;

            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(132) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Border imgHost = new Border();
            imgHost.CornerRadius = new CornerRadius(9, 9, 0, 0);
            imgHost.ClipToBounds = true;
            imgHost.Background = new SolidColorBrush(Color.FromRgb(18, 19, 24));

            Grid imgStack = new Grid();
            if (a.Covers.Count > 0)
            {
                Image img = new Image();
                img.Stretch = Stretch.UniformToFill;
                imgStack.Children.Add(img);
                LoadRemoteImageAuto(img, Bili.Variant(a.Covers[0], "364w_205h_1c.jpg"),
                                    "https://www.bilibili.com/", 364);

                // 多图角标，让人一眼知道这是图集不是单张
                Border badge = new Border();
                badge.Background = new SolidColorBrush(Color.FromArgb(210, 12, 13, 18));
                badge.CornerRadius = new CornerRadius(9);
                badge.Padding = new Thickness(7, 2, 7, 2);
                badge.Margin = new Thickness(8, 8, 0, 0);
                badge.HorizontalAlignment = HorizontalAlignment.Left;
                badge.VerticalAlignment = VerticalAlignment.Top;
                TextBlock bt = new TextBlock();
                bt.Text = a.Covers.Count > 1 ? "图集 · " + a.Covers.Count + " 张封面" : "图集";
                bt.FontSize = 10.5;
                bt.Foreground = new SolidColorBrush(Color.FromRgb(226, 233, 244));
                badge.Child = bt;
                imgStack.Children.Add(badge);
            }
            imgHost.Child = imgStack;
            Grid.SetRow(imgHost, 0);
            g.Children.Add(imgHost);

            Grid info = new Grid();
            info.Margin = new Thickness(9, 5, 9, 6);
            info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock t = new TextBlock();
            t.Text = DisplayTitle(a.Title);
            t.FontSize = 12.5;
            t.Foreground = TextMain;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            RegisterTitle(a.Title, t);
            Grid.SetRow(t, 0);
            info.Children.Add(t);

            TextBlock meta = new TextBlock();
            string m = a.Author;
            if (!string.IsNullOrEmpty(a.Category)) m += " · " + a.Category;
            if (a.View > 0) m += " · " + FormatCount(a.View) + " 阅读";
            meta.Text = m;
            meta.FontSize = 10.5;
            meta.Foreground = TextDim;
            meta.Margin = new Thickness(0, 2, 0, 0);
            meta.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetRow(meta, 1);
            info.Children.Add(meta);

            Grid.SetRow(info, 1);
            g.Children.Add(info);

            card.Child = g;

            card.MouseEnter += delegate { card.Background = BgCardHover; };
            card.MouseLeave += delegate { card.Background = BgCard; };

            BiliArticle captured = a;
            card.MouseLeftButtonUp += delegate { ShowArticlePreview(captured); };
            return card;
        }

        /// <summary>专栏卡片缩略图：远程 364 宽，失败就留深色底，不弹错</summary>
        void LoadRemoteImageAuto(Image img, string url, string referer, int decodeWidth)
        {
            if (string.IsNullOrEmpty(url)) return;
            LoadRemoteImage(img, url, decodeWidth, referer, delegate { });
        }

        Border BuildOnlineCard(OnlineItem o)
        {
            Border card = new Border();
            card.Width = 244;
            card.Height = 182;
            card.Margin = new Thickness(7);
            card.CornerRadius = new CornerRadius(10);
            card.Background = BgCard;
            card.BorderBrush = BorderIdle;
            card.BorderThickness = new Thickness(1);
            card.Cursor = Cursors.Hand;

            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(132) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Border imgHost = new Border();
            imgHost.CornerRadius = new CornerRadius(9, 9, 0, 0);
            imgHost.ClipToBounds = true;
            imgHost.Background = new SolidColorBrush(Color.FromRgb(34, 36, 44));

            Image img = new Image();
            img.Stretch = Stretch.UniformToFill;
            imgHost.Child = img;
            LoadRemoteImage(img, o.ThumbUrl);
            g.Children.Add(imgHost);

            Border badge = new Border();
            badge.Background = new SolidColorBrush(Color.FromArgb(220, 46, 92, 168));
            badge.CornerRadius = new CornerRadius(0, 9, 0, 8);
            badge.Padding = new Thickness(7, 2, 7, 2);
            badge.HorizontalAlignment = HorizontalAlignment.Right;
            badge.VerticalAlignment = VerticalAlignment.Top;

            TextBlock bt = new TextBlock();
            bt.Text = downloading.Contains(o.Slug) ? "下载中" : "在线";
            bt.FontSize = 10.5;
            bt.Foreground = Brushes.White;
            badge.Child = bt;
            g.Children.Add(badge);

            DockPanel bottom = new DockPanel();
            bottom.Margin = new Thickness(11, 7, 11, 7);
            bottom.LastChildFill = true;

            TextBlock tag = new TextBlock();
            tag.Text = "1080P";
            tag.FontSize = 11;
            tag.Foreground = Accent;
            tag.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(tag, Dock.Right);

            TextBlock name = new TextBlock();
            RegisterTitle(o.Title, name);
            name.FontSize = 13;
            name.Foreground = TextMain;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.VerticalAlignment = VerticalAlignment.Center;

            bottom.Children.Add(tag);
            bottom.Children.Add(name);
            Grid.SetRow(bottom, 1);
            g.Children.Add(bottom);

            card.Child = g;
            card.MouseEnter += delegate { card.Background = BgCardHover; card.BorderBrush = BorderHover; };
            card.MouseLeave += delegate { card.Background = BgCard; card.BorderBrush = BorderIdle; };

            OnlineItem captured = o;
            card.MouseLeftButtonUp += delegate { ShowOnlinePreview(captured); };
            return card;
        }

        /// <summary>B 站结果卡：封面用 480 宽（约 30-60KB），点开才拉 1280 宽大图</summary>
        Border BuildBiliCard(BiliItem b)
        {
            Border card = new Border();
            card.Width = 244;
            card.Height = 182;
            card.Margin = new Thickness(7);
            card.CornerRadius = new CornerRadius(10);
            card.Background = BgCard;
            card.BorderBrush = BorderIdle;
            card.BorderThickness = new Thickness(1);
            card.Cursor = Cursors.Hand;

            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(132) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Border imgHost = new Border();
            imgHost.CornerRadius = new CornerRadius(9, 9, 0, 0);
            imgHost.ClipToBounds = true;
            imgHost.Background = new SolidColorBrush(Color.FromRgb(34, 36, 44));

            Image img = new Image();
            img.Stretch = Stretch.UniformToFill;
            imgHost.Child = img;
            LoadRemoteImage(img, b.PicCard, 364, "https://www.bilibili.com/", null);
            g.Children.Add(imgHost);

            Border badge = new Border();
            badge.Background = new SolidColorBrush(Color.FromArgb(220, 200, 60, 90));
            badge.CornerRadius = new CornerRadius(0, 9, 0, 8);
            badge.Padding = new Thickness(7, 2, 7, 2);
            badge.HorizontalAlignment = HorizontalAlignment.Right;
            badge.VerticalAlignment = VerticalAlignment.Top;

            TextBlock bt = new TextBlock();
            bt.Text = downloading.Contains(b.Bvid) ? "下载中" : "B站";
            bt.FontSize = 10.5;
            bt.Foreground = Brushes.White;
            badge.Child = bt;
            g.Children.Add(badge);

            DockPanel bottom = new DockPanel();
            bottom.Margin = new Thickness(11, 7, 11, 7);
            bottom.LastChildFill = true;

            TextBlock len = new TextBlock();
            len.Text = FormatDuration(b.DurationSec);
            len.FontSize = 11;
            len.Foreground = Accent;
            len.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(len, Dock.Right);

            TextBlock name = new TextBlock();
            RegisterTitle(b.Title, name);
            name.FontSize = 13;
            name.Foreground = TextMain;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.VerticalAlignment = VerticalAlignment.Center;

            bottom.Children.Add(len);
            bottom.Children.Add(name);
            Grid.SetRow(bottom, 1);
            g.Children.Add(bottom);

            card.Child = g;
            card.MouseEnter += delegate { card.Background = BgCardHover; card.BorderBrush = BorderHover; };
            card.MouseLeave += delegate { card.Background = BgCard; card.BorderBrush = BorderIdle; };

            BiliItem captured = b;
            card.MouseLeftButtonUp += delegate { ShowBiliPreview(captured); };
            return card;
        }

        static string FormatDuration(int seconds)
        {
            if (seconds <= 0) return "";
            int m = seconds / 60;
            int s = seconds % 60;
            if (m >= 60) return (m / 60) + ":" + (m % 60).ToString("00") + ":" + s.ToString("00");
            return m + ":" + s.ToString("00");
        }

        // 缩略图缓存：刷新列表时卡片会重建，没有缓存就会把同样的 24 张图重复下载一遍
        static readonly Dictionary<string, BitmapImage> ThumbCache = new Dictionary<string, BitmapImage>();

        void LoadRemoteImage(Image img, string url)
        {
            LoadRemoteImage(img, url, 364, null, null);
        }

        /// <summary>远程图片：带内存缓存，卡片与预览用不同解码宽度，互不影响</summary>
        void LoadRemoteImage(Image img, string url, int decodeWidth, string referer, Action onDone)
        {
            if (string.IsNullOrEmpty(url))
            {
                if (onDone != null) onDone();
                return;
            }

            string key = url + "#" + decodeWidth;
            BitmapImage cached;
            if (ThumbCache.TryGetValue(key, out cached))
            {
                img.Source = cached;
                if (onDone != null) onDone();
                return;
            }

            Task.Run(delegate
            {
                try
                {
                    byte[] data = FetchBytes(url, referer, 12000);

                    BitmapImage bi = new BitmapImage();
                    using (MemoryStream ms = new MemoryStream(data))
                    {
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.StreamSource = ms;
                        if (decodeWidth > 0) bi.DecodePixelWidth = decodeWidth;
                        bi.EndInit();
                    }
                    bi.Freeze();

                    lock (ThumbCache) { if (!ThumbCache.ContainsKey(key)) ThumbCache[key] = bi; }
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        img.Source = bi;
                        if (onDone != null) onDone();
                    }));
                }
                catch (Exception ex)
                {
                    Log.Note("图片加载失败 " + url + " :: " + ex.Message);
                    if (onDone != null)
                    {
                        try { Dispatcher.BeginInvoke(new Action(onDone)); }
                        catch { }
                    }
                }
            });
        }

        /// <summary>带超时的字节下载：WebClient 默认 100 秒超时，挂住时界面就是干等</summary>
        static byte[] FetchBytes(string url, string referer, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";
            if (!string.IsNullOrEmpty(referer)) req.Referer = referer;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs + 4000;
            req.KeepAlive = false;
            Net.Apply(req);
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                using (Stream s = resp.GetResponseStream())
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buf = new byte[32768];
                        while (true)
                        {
                            int n = s.Read(buf, 0, buf.Length);
                            if (n <= 0) break;
                            ms.Write(buf, 0, n);
                        }
                        return ms.ToArray();
                    }
                }
            }
        }

        void DownloadOnline(OnlineItem o)
        {
            if (downloading.Contains(o.Slug))
            {
                SetStatus("这张正在下载，别重复点：" + o.Title);
                return;
            }

            downloading.Add(o.Slug);
            RefreshList();
            SetStatus("开始下载：" + DisplayTitle(o.Title) + "（1080P 约 10MB）…");

            string tag = TagNames.GuessTag(onlineKeyword);
            OnlineItem captured = o;

            Task.Run(delegate
            {
                try
                {
                    WallpaperItem it = Online.Install(captured, app.Config.LibRoot, tag, delegate(string msg)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate { SetStatus(msg); }));
                    });

                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        downloading.Remove(captured.Slug);
                        app.ReloadLibrary();
                        showingOnline = false;
                        filter = "";
                        searchBox.Text = "";
                        RefreshList();
                        SetStatus("已入库：" + DisplayTitle(it.Title) + "　" + Math.Round(it.Bytes / 1048576.0, 1) + "MB　点卡片即可设为壁纸");
                    }));
                }
                catch (Exception ex)
                {
                    string msg = ex.Message;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        downloading.Remove(captured.Slug);
                        RefreshList();
                        SetStatus("下载失败：" + msg);
                    }));
                }
            });
        }

        // ---------------- 放大预览 ----------------

        class PreviewData
        {
            public string Title = "";
            public string Meta = "";
            public string RemoteUrl = "";
            public string RemoteReferer = "";
            public string LocalPath = "";
            public string PageUrl = "";
            public string PrimaryText = "下载并入库";
            public Action OnPrimary;
            public string SecondText = "";       // 可选的第二个按钮（静态图的「动态播放」）
            public Action OnSecond;
            public string ThirdText = "";        // 可选的第三个按钮（换一种动效）
            public Action OnThird;
            public Action OnPrev;       // 图集翻页（可空）
            public Action OnNext;
            public bool IsGallery;
        }

        void ShowPreview(PreviewData d)
        {
            overlay.Children.Clear();
            galleryOpen = d.IsGallery;

            Border panel = new Border();
            panel.Background = new SolidColorBrush(Color.FromRgb(20, 21, 27));
            panel.BorderBrush = new SolidColorBrush(Color.FromRgb(54, 58, 72));
            panel.BorderThickness = new Thickness(1);
            panel.CornerRadius = new CornerRadius(14);
            panel.Margin = new Thickness(70, 34, 70, 34);
            panel.MaxWidth = Math.Max(640, Width - 150);
            panel.HorizontalAlignment = HorizontalAlignment.Center;
            panel.VerticalAlignment = VerticalAlignment.Center;
            // 点面板本身不关，点周围的暗底才关
            panel.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };

            Grid body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Border imgHost = new Border();
            imgHost.Background = new SolidColorBrush(Color.FromRgb(11, 12, 16));
            imgHost.CornerRadius = new CornerRadius(13, 13, 0, 0);
            imgHost.ClipToBounds = true;
            imgHost.MinHeight = 300;
            imgHost.MaxHeight = 620;

            Grid imgStack = new Grid();
            Image img = new Image();
            img.Stretch = Stretch.Uniform;
            img.Margin = new Thickness(6);
            imgStack.Children.Add(img);

            TextBlock loading = new TextBlock();
            loading.Text = "正在加载大图…";
            loading.FontSize = 13;
            loading.Foreground = TextDim;
            loading.HorizontalAlignment = HorizontalAlignment.Center;
            loading.VerticalAlignment = VerticalAlignment.Center;
            imgStack.Children.Add(loading);

            imgHost.Child = imgStack;
            Grid.SetRow(imgHost, 0);
            body.Children.Add(imgHost);

            if (!string.IsNullOrEmpty(d.LocalPath))
            {
                BitmapImage bi = LoadImage(d.LocalPath, 1400);
                if (bi != null)
                {
                    img.Source = bi;
                    loading.Visibility = Visibility.Collapsed;
                }
                else
                {
                    loading.Text = "这张图打不开：" + d.LocalPath;
                }
            }
            else
            {
                Action done = delegate { loading.Visibility = Visibility.Collapsed; };
                LoadRemoteImage(img, d.RemoteUrl, 1280, d.RemoteReferer, done);
            }

            DockPanel bar = new DockPanel();
            bar.Margin = new Thickness(20, 14, 20, 16);
            bar.LastChildFill = true;

            StackPanel btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            btns.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(btns, Dock.Right);

            Button primary = MakeButton(d.PrimaryText);
            primary.Padding = new Thickness(20, 8, 20, 8);
            primary.Background = new SolidColorBrush(Color.FromRgb(46, 92, 168));
            primary.BorderBrush = new SolidColorBrush(Color.FromRgb(64, 118, 208));
            primary.Foreground = Brushes.White;
            Action act = d.OnPrimary;
            primary.Click += delegate
            {
                HidePreview();
                if (act != null) act();
            };

            Button close = MakeButton("关闭");
            close.Click += delegate { HidePreview(); };

            if (d.OnPrev != null)
            {
                Button prev = MakeButton("‹ 上一张");
                Action p = d.OnPrev;
                prev.Click += delegate { HidePreview(); p(); };
                btns.Children.Add(prev);
            }
            if (d.OnNext != null)
            {
                Button next = MakeButton("下一张 ›");
                Action n = d.OnNext;
                next.Click += delegate { HidePreview(); n(); };
                btns.Children.Add(next);
            }

            if (!string.IsNullOrEmpty(d.SecondText) && d.OnSecond != null)
            {
                Button sec = MakeButton(d.SecondText);
                Action a2 = d.OnSecond;
                sec.Click += delegate { HidePreview(); a2(); };
                btns.Children.Add(sec);
            }
            if (!string.IsNullOrEmpty(d.ThirdText) && d.OnThird != null)
            {
                Button tr = MakeButton(d.ThirdText);
                Action a3 = d.OnThird;
                tr.Click += delegate { HidePreview(); a3(); };
                btns.Children.Add(tr);
            }
            btns.Children.Add(primary);
            if (!string.IsNullOrEmpty(d.PageUrl))
            {
                Button open = MakeButton("在浏览器打开");
                string url = d.PageUrl;
                open.Click += delegate
                {
                    try { Process.Start(url); }
                    catch (Exception ex) { SetStatus("打开失败：" + ex.Message); }
                };
                btns.Children.Add(open);
            }
            btns.Children.Add(close);
            bar.Children.Add(btns);

            StackPanel texts = new StackPanel();
            texts.VerticalAlignment = VerticalAlignment.Center;

            TextBlock t1 = new TextBlock();
            t1.Text = d.Title;
            t1.FontSize = 16;
            t1.FontWeight = FontWeights.SemiBold;
            t1.Foreground = TextMain;
            t1.TextTrimming = TextTrimming.CharacterEllipsis;

            TextBlock t2 = new TextBlock();
            t2.Text = d.Meta;
            t2.FontSize = 12;
            t2.Foreground = TextDim;
            t2.Margin = new Thickness(0, 5, 0, 0);
            t2.TextWrapping = TextWrapping.Wrap;

            texts.Children.Add(t1);
            texts.Children.Add(t2);
            bar.Children.Add(texts);

            Grid.SetRow(bar, 1);
            body.Children.Add(bar);

            panel.Child = body;
            overlay.Children.Add(panel);
            overlay.Visibility = Visibility.Visible;
        }

        void HidePreview()
        {
            if (overlay == null) return;
            loadToken++;                      // 关掉预览 = 丢掉还在路上的图集结果
            overlay.Visibility = Visibility.Collapsed;
            overlay.Children.Clear();
            galleryOpen = false;
        }

        void ShowOnlinePreview(OnlineItem o)
        {
            PreviewData d = new PreviewData();
            d.Title = DisplayTitle(o.Title);
            d.Meta = "壁纸站 · 预览图 1280×720 · 下载为 1080P 视频（约 10MB）";
            d.RemoteUrl = string.IsNullOrEmpty(o.PreviewUrl) ? o.ThumbUrl : o.PreviewUrl;
            d.PageUrl = o.Page;
            d.PrimaryText = "下载并入库";
            OnlineItem cap = o;
            d.OnPrimary = delegate { DownloadOnline(cap); };
            ShowPreview(d);
        }

        void ShowBiliPreview(BiliItem b)
        {
            string meta = "B 站 · " + b.Author;
            if (b.DurationSec > 0) meta += " · " + FormatDuration(b.DurationSec);
            if (b.Play > 0) meta += " · " + FormatCount(b.Play) + " 播放";
            meta += " · 下载 1080P（约 8-25MB，无音轨，省一半体积）";

            PreviewData d = new PreviewData();
            d.Title = DisplayTitle(b.Title);
            d.Meta = meta;
            d.RemoteUrl = string.IsNullOrEmpty(b.PicPreview) ? b.PicOrigin : b.PicPreview;
            d.RemoteReferer = "https://www.bilibili.com/";
            d.PageUrl = b.Page;
            d.PrimaryText = "下载并入库";
            BiliItem cap = b;
            d.OnPrimary = delegate { DownloadBili(cap); };
            ShowPreview(d);
        }

        // ---------------- 专栏图集预览（可翻页） ----------------

        /// <summary>点专栏卡片：先拉正文里的全部原图，再进图集预览</summary>
        void ShowArticlePreview(BiliArticle a)
        {
            if (a == null) return;
            if (a.Images != null && a.Images.Count > 0)
            {
                ShowArticleGallery(a, 0);
                return;
            }
            StartArticleLoad(a, false);
        }

        /// <summary>
        /// 读图集。失败不再只写状态栏（状态栏被 overlay 盖住，等于没提示），
        /// 而是把原因直接摆在面板里，给「重试 / 在浏览器打开 / 取消」三个出口。
        /// </summary>
        void StartArticleLoad(BiliArticle a, bool retry)
        {
            loadToken++;
            int token = loadToken;
            BiliArticle cap = a;

            ShowArticleLoading(a, retry ? "正在重试…" : "正在读取这篇专栏里的全部图片…（一篇通常十几到几十张）", false);

            Task.Run(delegate
            {
                List<string> imgs = null;
                string note = "";
                string err = null;
                try { imgs = Bili.LoadArticleImages(cap, out note); }
                catch (Exception ex) { err = ex.Message; }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (token != loadToken) return;      // 预览已经关了或换了，丢弃结果

                    if (imgs == null || imgs.Count == 0)
                    {
                        string msg = (err != null && err.Length > 0) ? err : (note.Length > 0 ? note : "没有读到图片");
                        ShowArticleLoading(cap, "读取失败：" + msg, true);
                        SetStatus("读取图集失败：" + msg);
                        return;
                    }

                    FilterPortrait(cap);
                    if (cap.Images == null || cap.Images.Count == 0)
                    {
                        ShowArticleLoading(cap, "这篇里的图全是竖版（手机壁纸），按你的设置不入库。", true);
                        return;
                    }
                    if (note.Length > 0) SetStatus(note);
                    ShowArticleGallery(cap, 0);
                }));
            });
        }

        /// <summary>竖版图只有用户手动勾了「过滤竖版」才丢。
        /// （以前默认丢，理由是「填充会裁两边」；现在竖图会自动适配屏幕，这个理由不成立了）</summary>
        void FilterPortrait(BiliArticle a)
        {
            if (a == null || a.Images == null) return;
            if (!app.Config.FilterPortrait) return;      // 设置里关掉了就照收

            List<string> keep = new List<string>();
            List<string> keepSizes = new List<string>();
            int dropped = 0;
            List<string> ss = a.Sizes == null ? new List<string>() : a.Sizes;

            for (int i = 0; i < a.Images.Count; i++)
            {
                string size = i < ss.Count ? ss[i] : "";
                int w = 0;
                int h = 0;
                SplitSize(size, out w, out h);
                if (w > 0 && h > 0 && h > w) { dropped++; continue; }
                keep.Add(a.Images[i]);
                keepSizes.Add(size);
            }

            a.Images = keep;
            a.Sizes = keepSizes;
            a.DroppedPortrait = dropped;
            Log.Note("图集 cv" + a.Cvid + "：滤掉竖版 " + dropped + " 张，留 " + keep.Count + " 张");
        }

        static void SplitSize(string s, out int w, out int h)
        {
            w = 0;
            h = 0;
            if (string.IsNullOrEmpty(s)) return;
            string[] p = s.Split('x');
            if (p.Length != 2) return;
            int.TryParse(p[0], out w);
            int.TryParse(p[1], out h);
        }

        /// <summary>加载 / 失败态：一个面板说清所有事，不再往被盖住的状态栏写字</summary>
        void ShowArticleLoading(BiliArticle a, string status, bool failed)
        {
            overlay.Children.Clear();
            galleryOpen = false;

            Border panel = new Border();
            panel.Background = new SolidColorBrush(Color.FromRgb(20, 21, 27));
            panel.BorderBrush = new SolidColorBrush(failed ? Color.FromRgb(120, 52, 60) : Color.FromRgb(54, 58, 72));
            panel.BorderThickness = new Thickness(1);
            panel.CornerRadius = new CornerRadius(14);
            panel.Padding = new Thickness(26, 22, 26, 20);
            panel.Width = 580;
            panel.HorizontalAlignment = HorizontalAlignment.Center;
            panel.VerticalAlignment = VerticalAlignment.Center;
            panel.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };

            StackPanel sp = new StackPanel();

            TextBlock title = new TextBlock();
            title.Text = DisplayTitle(a.Title);
            title.FontSize = 16;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = TextMain;
            title.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(title);

            TextBlock tip = new TextBlock();
            tip.Text = status;
            tip.FontSize = 13;
            tip.Margin = new Thickness(0, 12, 0, 0);
            tip.Foreground = failed ? new SolidColorBrush(Color.FromRgb(240, 149, 149)) : TextDim;
            tip.TextWrapping = TextWrapping.Wrap;
            tip.LineHeight = 21;
            sp.Children.Add(tip);

            TextBlock page = new TextBlock();
            page.Text = a.Page;
            page.FontSize = 11.5;
            page.Margin = new Thickness(0, 10, 0, 0);
            page.Foreground = new SolidColorBrush(Color.FromRgb(104, 110, 124));
            sp.Children.Add(page);

            StackPanel btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            btns.Margin = new Thickness(0, 18, 0, 0);

            if (failed)
            {
                Button again = MakeButton("重试");
                again.Padding = new Thickness(20, 8, 20, 8);
                again.Background = new SolidColorBrush(Color.FromRgb(46, 92, 168));
                again.BorderBrush = new SolidColorBrush(Color.FromRgb(64, 118, 208));
                again.Foreground = Brushes.White;
                BiliArticle cap = a;
                again.Click += delegate { StartArticleLoad(cap, true); };
                btns.Children.Add(again);
            }

            Button open = MakeButton("在浏览器打开");
            string url = a.Page;
            open.Click += delegate
            {
                try { Process.Start(url); }
                catch (Exception ex) { SetStatus("打开失败：" + ex.Message); }
            };
            btns.Children.Add(open);

            Button cancel = MakeButton("取消");
            cancel.Click += delegate { HidePreview(); };
            btns.Children.Add(cancel);

            sp.Children.Add(btns);
            panel.Child = sp;
            overlay.Children.Add(panel);
            overlay.Visibility = Visibility.Visible;
        }

        void ShowArticleGallery(BiliArticle a, int index)
        {
            if (a == null || a.Images == null || a.Images.Count == 0) return;
            if (index < 0) index = a.Images.Count - 1;
            if (index >= a.Images.Count) index = 0;

            artCurrent = a;
            artImages = a.Images;
            artIndex = index;

            PreviewData d = new PreviewData();
            d.IsGallery = true;
            d.Title = DisplayTitle(a.Title);

            string meta = "B 站专栏图集 · " + a.Author;
            if (!string.IsNullOrEmpty(a.Category)) meta += " · " + a.Category;
            meta += " · 第 " + (index + 1) + "/" + a.Images.Count + " 张";
            if (a.Sizes != null && index < a.Sizes.Count && !string.IsNullOrEmpty(a.Sizes[index]))
                meta += " · " + a.Sizes[index];
            if (a.DroppedPortrait > 0) meta += " · 已滤掉 " + a.DroppedPortrait + " 张竖版";
            meta += " · 下载为 1920 宽 JPG（约 100-300KB）";
            d.Meta = meta;

            d.RemoteUrl = Bili.Variant(a.Images[index], "1280w.jpg");
            d.RemoteReferer = "https://www.bilibili.com/";
            d.PageUrl = a.Page;
            d.PrimaryText = "下载这张入库";

            BiliArticle capA = a;
            int capIndex = index;
            d.OnPrimary = delegate { DownloadArticleImage(capA, capIndex); };

            if (a.Images.Count > 1)
            {
                d.OnPrev = delegate { ShowArticleGallery(capA, capIndex - 1); };
                d.OnNext = delegate { ShowArticleGallery(capA, capIndex + 1); };
            }
            ShowPreview(d);
        }

        void DownloadArticleImage(BiliArticle a, int index)
        {
            if (a == null || a.Images == null || index < 0 || index >= a.Images.Count) return;

            string url = a.Images[index];
            int total = a.Images.Count;
            string tag = TagNames.GuessTag(onlineKeyword);
            if (tag == "online" || tag == "bili" || tag == "biliart") tag = "anime";

            SetStatus("开始下载图集第 " + (index + 1) + "/" + total + " 张…");
            BiliArticle cap = a;
            int idx = index;

            Task.Run(delegate
            {
                try
                {
                    WallpaperItem it = Bili.InstallArticleImage(cap, url, idx, total, app.Config.LibRoot, tag,
                        delegate(string msg) { Dispatcher.BeginInvoke(new Action(delegate { SetStatus(msg); })); });

                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        app.ReloadLibrary();
                        showingOnline = false;
                        filter = "";
                        searchBox.Text = "";
                        RefreshList();
                        SetStatus("已入库：" + DisplayTitle(it.Title) + "　"
                            + Math.Round(it.Bytes / 1024.0, 0) + "KB　点卡片即可设为壁纸");
                    }));
                }
                catch (Exception ex)
                {
                    string msg = ex.Message;
                    Dispatcher.BeginInvoke(new Action(delegate { SetStatus("下载失败：" + msg); }));
                }
            });
        }

        /// <summary>
        /// 顶栏「✦ 飘动视频」：作用于当前选中的那张图。
        /// 已经有片子就直接播；没有就下一单，等云端出片后自动换上。
        /// 应用里不做本机动效，也不再去网页手工生成——动态只走云端这一条。
        /// </summary>
        // ---------------- 云端任务浮条 ----------------
        // 主人的问题：「如果需要等待也得弹个提示啊，不然我咋知道点了有没有在生成」。
        // 所以下单成功/已在队列/上次失败，一律把这条浮条亮出来：报已排队多久、
        // 报靠谁出片、给取消与重试。到货或超时也走这里，不让人一直盯着猜。

        /// <summary>
        /// 点击「云端生成」的第一画面：选模型 + 看大概花费 / 预计等待，确认才提交。
        /// 没配好服务商或 Key 时不写工单、不假排队，直接给「去设置」——
        /// 以前那种「点了挂两天等一个不会来的外部程序」就是「点了不能用」的根因。
        /// </summary>
        public void CloudChoose(string id, string title, bool ready, GenApi.Provider provider,
                                string modelId, string resolution, int seconds)
        {
            cloudId = id == null ? "" : id;
            cloudMode = 3;
            cloudHold = 0;
            cloudReady = ready;
            cloudProvider = provider;
            cloudResolution = string.IsNullOrEmpty(resolution) ? "720P" : resolution;
            cloudSeconds = (seconds >= 1 && seconds <= CloudGen.MaxSeconds) ? seconds : CloudGen.MaxSeconds;

            cloudTitle.Text = "☁ 云端生成 · 确认下单";
            cloudTitle.Foreground = new SolidColorBrush(Color.FromRgb(228, 217, 255));
            cloudToast.BorderBrush = new SolidColorBrush(Color.FromRgb(138, 112, 196));
            cloudStatus.Foreground = new SolidColorBrush(Color.FromRgb(206, 196, 240));
            cloudName.Text = DisplayTitle(title);

            List<GenModelInfo> list = GenModels.For(provider);
            cloudModelBox.Items.Clear();
            for (int i = 0; i < list.Count; i++)
                cloudModelBox.Items.Add(list[i].Label + "　"
                    + GenModels.CostText(list[i], cloudResolution, cloudSeconds)
                    + "　" + GenModels.WaitText(list[i]));
            GenModelInfo want = GenModels.Resolve(provider, modelId);
            int ix = 0;
            for (int i = 0; i < list.Count; i++) if (list[i].Id == want.Id) { ix = i; break; }
            if (list.Count > 0) cloudModelBox.SelectedIndex = ix;

            cloudModelBox.Visibility = Visibility.Visible;
            cloudEstimate.Visibility = Visibility.Visible;
            cloudMain.Content = ready ? "开始生成" : "去设置";
            cloudMain.Visibility = Visibility.Visible;
            cloudSettings.Content = "取消";
            cloudSettings.Visibility = Visibility.Visible;
            cloudJobs.Visibility = Visibility.Collapsed;
            cloudToast.Visibility = Visibility.Visible;
            UpdateCloudChoiceEstimate();
        }

        GenModelInfo CloudPickedModel()
        {
            List<GenModelInfo> list = GenModels.For(cloudProvider);
            int i = cloudModelBox == null ? -1 : cloudModelBox.SelectedIndex;
            if (i >= 0 && i < list.Count) return list[i];
            return list.Count > 0 ? list[0] : null;
        }

        /// <summary>模型一换，金额和等待时间立刻跟着变（不是写死的一行字）</summary>
        void UpdateCloudChoiceEstimate()
        {
            GenModelInfo m = CloudPickedModel();
            if (m == null) return;
            cloudEstimate.Text = "大概花费 " + GenModels.CostText(m, cloudResolution, cloudSeconds)
                + "（" + m.Price(cloudResolution).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                + " 元/秒 × " + cloudSeconds + " 秒，" + cloudResolution + "）\n"
                + "预计等待 " + GenModels.WaitText(m) + "　·　模型 " + m.Id;
            // 到货 / 失败态复用这一行；不带服务商前缀，402px 宽的浮条放得下不折行
            cloudInfoLine = m.Id + " · " + cloudResolution + " · " + cloudSeconds
                          + " 秒 · " + GenModels.CostText(m, cloudResolution, cloudSeconds);
            cloudStatus.Text = cloudReady
                ? "点「开始生成」才会提交，提交后开始计费"
                : "还没填 API Key，现在不会扣费、也不会排队";
            cloudSub.Text = cloudReady
                ? "提交后自动轮询，出片下载完成会自己换上桌面。价格为公开单价估算，以账单为准。"
                : "点「去设置」选服务商、填 Key；配置好后这里会显示模型、预计等待和大概花费。";
        }

        /// <summary>下单成功（或重复点一张已在队列的）→ 等待中</summary>
        public void CloudQueued(string id)
        {
            cloudId = id == null ? "" : id;
            cloudMode = 0;
            cloudHold = 0;
            cloudTitle.Text = "☁ 云端生成";
            cloudTitle.Foreground = new SolidColorBrush(Color.FromRgb(228, 217, 255));
            cloudToast.BorderBrush = new SolidColorBrush(Color.FromRgb(138, 112, 196));
            cloudStatus.Foreground = new SolidColorBrush(Color.FromRgb(206, 196, 240));
            cloudModelBox.Visibility = Visibility.Collapsed;
            cloudEstimate.Visibility = Visibility.Visible;
            cloudMain.Content = "取消这单";
            cloudSettings.Content = "怎么生成？";
            cloudMain.Visibility = Visibility.Visible;
            cloudSettings.Visibility = Visibility.Visible;
            cloudJobs.Visibility = Visibility.Visible;
            cloudToast.Visibility = Visibility.Visible;
            UpdateCloudToast();
            StartCloudTimer();
        }

        /// <summary>
        /// 11-14：队列里躺着一张「没配 API、没有进程会提交」的工单。
        /// 以前 StateOf 一律报 Queued → 界面写「生成中」，主人一眼看穿是假的。
        /// 现在明说「待人工处理」，并把工单 JSON 摆到眼前，让主人自己决定删掉还是补 Key。
        /// </summary>
        public void CloudManual(string id, string title)
        {
            cloudId = id == null ? "" : id;
            cloudMode = 4;
            cloudHold = 0;
            cloudTitle.Text = "⏸ 云端生成 · 待人工处理";
            cloudTitle.Foreground = new SolidColorBrush(Color.FromRgb(236, 196, 130));
            cloudToast.BorderBrush = new SolidColorBrush(Color.FromRgb(168, 132, 68));
            cloudName.Text = DisplayTitle(title);
            cloudStatus.Foreground = new SolidColorBrush(Color.FromRgb(236, 196, 130));
            cloudStatus.Text = "× 这单不会自动出片：还没选服务商 / 没填 API Key";
            cloudSub.Text = "点「查看工单」打开 JSON（可直接删掉）；或点「怎么生成？」填好服务商和 Key。"
                          + "\r\n已经有片子就直接放进 videos\\，程序会自动认出来，不用重新生成。\r\n"
                          + "工单目录：" + CloudGen.QueueDir;
            cloudModelBox.Visibility = Visibility.Collapsed;
            cloudEstimate.Visibility = Visibility.Collapsed;
            cloudMain.Content = "取消这单";
            cloudSettings.Content = "怎么生成？";
            cloudMain.Visibility = Visibility.Visible;
            cloudSettings.Visibility = Visibility.Visible;
            cloudJobs.Visibility = Visibility.Visible;
            cloudToast.Visibility = Visibility.Visible;
            StartCloudTimer();
        }

        /// <summary>片子到货 → 成功态，留 8 秒让人看清，然后自动收起</summary>
        public void CloudArrived(string title, bool switched)
        {
            cloudMode = 1;
            cloudHold = 0;
            cloudTitle.Text = "☁ 云端生成";
            cloudTitle.Foreground = new SolidColorBrush(Color.FromRgb(140, 214, 160));
            cloudToast.BorderBrush = new SolidColorBrush(Color.FromRgb(86, 160, 110));
            cloudName.Text = DisplayTitle(title);
            cloudStatus.Foreground = new SolidColorBrush(Color.FromRgb(140, 214, 160));
            cloudStatus.Text = switched ? "✔ 已到货，已经换上桌面了" : "✔ 已到货，随时可以切过去";
            cloudSub.Text = "3 秒短片已经存进 videos\\，卡片上会多一个「✦ 飘动」角标。";
            cloudModelBox.Visibility = Visibility.Collapsed;
            cloudEstimate.Visibility = Visibility.Visible;
            if (cloudInfoLine.Length > 0) cloudEstimate.Text = cloudInfoLine;
            cloudMain.Content = "知道了";
            cloudMain.Visibility = Visibility.Visible;
            cloudSettings.Visibility = Visibility.Collapsed;
            cloudJobs.Visibility = Visibility.Collapsed;
            cloudToast.Visibility = Visibility.Visible;
            StartCloudTimer();
        }

        /// <summary>失败 / 超时 → 失败态，给重试按钮</summary>
        public void CloudFailed(string id, string title, string why)
        {
            cloudId = id == null ? "" : id;
            cloudMode = 2;
            cloudHold = 0;
            cloudTitle.Text = "☁ 云端生成";
            cloudTitle.Foreground = new SolidColorBrush(Color.FromRgb(236, 148, 148));
            cloudToast.BorderBrush = new SolidColorBrush(Color.FromRgb(168, 78, 78));
            cloudName.Text = DisplayTitle(title);
            cloudStatus.Foreground = new SolidColorBrush(Color.FromRgb(236, 148, 148));
            cloudStatus.Text = "× 没出片：" + why;
            cloudSub.Text = "点「重试」再下一单（会重新显示模型与费用）；或者看「怎么生成？」确认通道是否配好。";
            cloudModelBox.Visibility = Visibility.Collapsed;
            cloudEstimate.Visibility = Visibility.Visible;
            if (cloudInfoLine.Length > 0) cloudEstimate.Text = cloudInfoLine;
            cloudMain.Content = "重试";
            cloudMain.Visibility = Visibility.Visible;
            cloudSettings.Visibility = Visibility.Visible;
            cloudJobs.Visibility = Visibility.Visible;
            cloudToast.Visibility = Visibility.Visible;
            StartCloudTimer();
        }

        void StartCloudTimer()
        {
            if (cloudTimer == null)
            {
                cloudTimer = new System.Windows.Threading.DispatcherTimer();
                cloudTimer.Interval = TimeSpan.FromSeconds(1);
                cloudTimer.Tick += delegate { UpdateCloudToast(); };
            }
            cloudTimer.Stop();
            cloudTimer.Start();
        }

        /// <summary>每秒刷一次：排队态读真实队列，成功/失败态数满 8 秒收起</summary>
        void UpdateCloudToast()
        {
            if (cloudToast == null || cloudToast.Visibility != Visibility.Visible) return;

            // 确认态等主人自己决定，不自动收起、也不每秒重画（下拉会被刷新打断）
            // mode=4（待人工处理）同理：文案不能被倒计时刷掉，也不该 8 秒就消失。
            if (cloudMode == 3 || cloudMode == 4) return;

            if (cloudMode != 0)
            {
                if (++cloudHold >= 8)
                {
                    HideCloudToast();
                    if (cloudTimer != null) cloudTimer.Stop();
                }
                return;
            }

            if (app == null) return;
            App.CloudSnap snap = app.CloudSnapshot();
            if (snap.Pending <= 0)
            {
                // 队列空了又没走「到货」分支，说明单子被取消了或者被外部删掉了
                HideCloudToast();
                if (cloudTimer != null) cloudTimer.Stop();
                return;
            }

            if (!string.IsNullOrEmpty(snap.Id)) cloudId = snap.Id;
            cloudName.Text = DisplayTitle(snap.Title);

            string[] spin = new string[] { "◐", "◓", "◑", "◒" };
            string phase = string.IsNullOrEmpty(snap.Phase) ? "等待中" : snap.Phase;
            cloudStatus.Text = spin[cloudSpin++ % 4] + "  " + phase
                             + " · 已等 " + GenModels.ElapsedText(snap.WaitSeconds)
                             + (snap.Pending > 1 ? "　（共 " + snap.Pending + " 个在等）" : "");
            cloudEstimate.Text = snap.EstimateText;
            cloudSub.Text = snap.InfoLine;
        }

        void HideCloudToast()
        {
            if (cloudToast != null) cloudToast.Visibility = Visibility.Collapsed;
        }

        void OnCloudMainClick(object sender, RoutedEventArgs e)
        {
            string id = cloudId;
            if (cloudMode == 3)
            {
                if (!cloudReady)         // 没配 Key：直接带去设置，绝不下单
                {
                    HideCloudToast();
                    if (app != null) app.OpenSettings();
                    return;
                }
                GenModelInfo m = CloudPickedModel();
                string picked = m == null ? "" : m.Id;
                HideCloudToast();
                if (app != null) app.StartCloud(id, picked, cloudResolution);
                return;
            }
            if (cloudMode == 2)
            {
                HideCloudToast();
                if (app != null) app.RetryCloud(id);
                return;
            }
            if (cloudMode == 0 || cloudMode == 4)
            {
                HideCloudToast();
                if (app != null) app.CancelCloud(id);
                return;
            }
            HideCloudToast();
            if (cloudTimer != null) cloudTimer.Stop();
        }

        // ---------------- 自检钩子：让 --cloudflowtest 能真点按钮，不靠外部截图 ----------------

        /// <summary>真实触发确认浮条的主按钮 Click（RaiseEvent 走同一个处理器）</summary>
        public string SelfTestCloudConfirm()
        {
            if (cloudMode != 3) return "× 当前不是确认态（mode=" + cloudMode + "）";
            string label = cloudMain.Content == null ? "" : cloudMain.Content.ToString();
            bool ready = cloudReady;
            cloudMain.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return "√ 点击「" + label + "」 ready=" + ready + " → mode=" + cloudMode + " id=" + cloudId;
        }

        /// <summary>真实切换模型下拉，核对费用/等待文案随之变化</summary>
        public string SelfTestCloudModelPick(string modelId)
        {
            if (cloudMode != 3) return "× 不在确认态";
            List<GenModelInfo> list = GenModels.For(cloudProvider);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Id == modelId)
                {
                    cloudModelBox.SelectedIndex = i;
                    return "√ 选中 " + modelId + " → " + cloudEstimate.Text.Replace("\n", "｜");
                }
            }
            return "× 目录里没有 " + modelId;
        }

        /// <summary>浮条当前状态，供自检断言「等待中 → 已到货」这条状态迁移</summary>
        public string SelfTestCloudState()
        {
            string main = cloudMain == null || cloudMain.Content == null ? "" : cloudMain.Content.ToString();
            return "mode=" + cloudMode + "｜主按钮=" + main + "｜" + cloudStatus.Text
                 + "｜" + cloudEstimate.Text + "｜" + cloudSub.Text;
        }

        /// <summary>自检用：收起浮条，好接着测下一种状态</summary>
        public void HideCloudToastForTest()
        {
            HideCloudToast();
            if (cloudTimer != null) cloudTimer.Stop();
        }

        static string WaitText(long sec)
        {
            if (sec < 0) sec = 0;
            if (sec < 60) return sec + " 秒";
            if (sec < 3600) return (sec / 60) + " 分 " + (sec % 60) + " 秒";
            return (sec / 3600) + " 小时 " + ((sec % 3600) / 60) + " 分";
        }

        void CloudForCurrent()
        {
            WallpaperItem it = currentItem;
            if (it == null)
            {
                SetStatus("先在列表里点一张图，再点「✦ 飘动视频」");
                return;
            }
            if (it.IsVideo)
            {
                SetStatus("这张本身就是视频壁纸，不用再配飘动视频");
                return;
            }
            if (app.HasCloudVideo(it))
            {
                app.SetMotionFx(it, 2);
                SetStatus("正在播这张的飘动视频：" + DisplayTitle(it.Title));
                return;
            }
            app.GenCloud(it);
        }

        void ShowLocalPreview(WallpaperItem it)
        {
            string meta = "本地图库 · " + TagNames.Cn(it.Tag);
            if (it.Duration > 0) meta += " · " + FormatDuration((int)Math.Round(it.Duration));
            meta += " · " + Math.Round(it.Bytes / 1048576.0, 1) + "MB";
            if (it.IsVideo) meta += " · 动态视频（桌面播放引擎这版只做挂载验证）";
            else if (app.HasCloudVideo(it)) meta += " · 已配飘动视频，可播"
                                                  + (it.Motion ? "（当前已设为动态）" : "");
            else
            {
                CloudGen.State st = CloudGen.StateOf(app.Config, it);
                if (st == CloudGen.State.Queued) meta += " · 云端生成中，好了会自动换上";
                else if (st == CloudGen.State.Manual) meta += " · 有一张不会自动出片的旧工单（可查看/删除）";
                else if (st == CloudGen.State.Failed) meta += " · 上次生成失败了，可以重试";
                // 11-14：新图被旧版误标成动态、其实一片都没有时，别再骗人「已设为动态播放」。
                if (it.Motion) meta += " · 标了动态但没找到片子（点「云端生成」）";
            }

            PreviewData d = new PreviewData();
            d.Title = DisplayTitle(it.Title);
            d.Meta = meta;
            d.LocalPath = (!string.IsNullOrEmpty(it.ImagePath) && File.Exists(it.ImagePath)) ? it.ImagePath : it.ThumbAbs;

            WallpaperItem cap = it;
            if (it.IsVideo)
            {
                d.PrimaryText = "跑挂载探测（6 秒）";
                d.OnPrimary = delegate { app.RunProbe(); };
            }
            else
            {
                d.PrimaryText = "设为壁纸";
                d.OnPrimary = delegate { app.ApplyItem(cap); };

                // 11-14 深夜：主人「云端生成功能按钮怎么没有了？」
                // 旧版只要 it.Motion 就把第二按钮换成「改回静态」，有片时又换成「播放」，
                // 结果「云端生成」整个入口都消失。现在按「有没有片」决定：
                // 有片也保留「重新生成一片」，没片时误标的动态仍可从第三按钮退回静态。
                bool hasVid = app.HasCloudVideo(cap);
                if (hasVid)
                {
                    d.SecondText = "▶ 播飘动视频";
                    d.OnSecond = delegate { app.SetMotionFx(cap, 2); };
                    d.ThirdText = "✦ 重新生成一片";
                    d.OnThird = delegate { app.RegenCloud(cap); };
                }
                else
                {
                    d.SecondText = "✦ 云端生成（3 秒）";
                    d.OnSecond = delegate { app.GenCloud(cap); };
                    if (cap.Motion)
                    {
                        d.ThirdText = "改回静态壁纸";
                        d.OnThird = delegate { app.ToggleMotion(cap); };
                    }
                }
            }
            ShowPreview(d);
        }

        static string FormatCount(long n)
        {
            if (n >= 10000) return Math.Round(n / 10000.0, 1) + "万";
            return n.ToString();
        }

        // ---------------- 标题中文化 ----------------

        public static string DisplayTitle(string origin)
        {
            string zh = Translate.Get(origin);
            return string.IsNullOrEmpty(zh) ? origin : zh;
        }

        void RegisterTitle(string origin, TextBlock tb)
        {
            tb.Text = DisplayTitle(origin);
            if (!Translate.NeedsTranslation(origin)) return;
            if (Translate.Get(origin) != null) return;
            titleLabels[Translate.Normalize(origin)] = tb;
        }

        void PrefetchTitles(List<string> titles)
        {
            Translate.Prefetch(titles, delegate(string en, string zh)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    TextBlock tb;
                    if (titleLabels.TryGetValue(en, out tb) && tb != null) tb.Text = zh;
                }));
            });
        }

        void DownloadBili(BiliItem b)
        {
            if (downloading.Contains(b.Bvid))
            {
                SetStatus("这个视频正在下载，别重复点：" + DisplayTitle(b.Title));
                return;
            }

            downloading.Add(b.Bvid);
            RefreshList();
            SetStatus("开始解析：" + DisplayTitle(b.Title) + "…");

            string tag = TagNames.GuessTag(onlineKeyword);
            if (tag == "online" || tag == "bili") tag = "bili";
            BiliItem cap = b;

            Task.Run(delegate
            {
                try
                {
                    WallpaperItem it = Bili.Install(cap, app.Config.LibRoot, tag, delegate(string msg)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate { SetStatus(msg); }));
                    });

                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        downloading.Remove(cap.Bvid);
                        app.ReloadLibrary();
                        showingOnline = false;
                        filter = "";
                        searchBox.Text = "";
                        RefreshList();
                        SetStatus("已入库：" + DisplayTitle(it.Title) + "　" + Math.Round(it.Bytes / 1048576.0, 1) + "MB　点卡片即可设为壁纸");
                    }));
                }
                catch (Exception ex)
                {
                    string msg = ex.Message;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        downloading.Remove(cap.Bvid);
                        RefreshList();
                        SetStatus("B 站下载失败：" + msg);
                    }));
                }
            });
        }

        // ---------------- 全局快捷键 ----------------

        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
        const int HK_PAUSE = 1, HK_NEXT = 2;
        const uint VK_SPACE = 0x20, VK_N = 0x4E;

        [DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        IntPtr hwndSelf;
        System.Windows.Interop.HwndSource hwndSrc;

        /// <summary>
        /// 窗口不在前台也能用：Ctrl+Alt+Space 暂停/继续，Ctrl+Alt+N 换下一张。
        /// 用的不是 Ctrl+Alt+方向键 —— 那个在部分显卡驱动上是「旋转屏幕」，会把人屏幕拧歪。
        /// </summary>
        void RegisterHotkeys()
        {
            hwndSelf = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwndSelf == IntPtr.Zero) return;
            hwndSrc = System.Windows.Interop.HwndSource.FromHwnd(hwndSelf);
            if (hwndSrc != null) hwndSrc.AddHook(WndProc);

            bool a = RegisterHotKey(hwndSelf, HK_PAUSE, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_SPACE);
            bool b = RegisterHotKey(hwndSelf, HK_NEXT, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_N);
            Log.Note("全局快捷键：暂停/继续=" + a + "  换下一张=" + b
                     + "（Ctrl+Alt+Space / Ctrl+Alt+N）");
            if (!a || !b) SetStatus("有快捷键被别的程序占用了（Ctrl+Alt+Space / Ctrl+Alt+N），其余功能照常用");
        }

        void UnregisterHotkeys()
        {
            if (hwndSelf == IntPtr.Zero) return;
            UnregisterHotKey(hwndSelf, HK_PAUSE);
            UnregisterHotKey(hwndSelf, HK_NEXT);
            if (hwndSrc != null) { hwndSrc.RemoveHook(WndProc); hwndSrc = null; }
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HK_PAUSE) { app.TogglePause(); handled = true; }
                else if (id == HK_NEXT) { app.RotateNext(); handled = true; }
            }
            return IntPtr.Zero;
        }

        // ---------------- 拖拽导入 ----------------

        /// <summary>把图片/文件夹/视频拖进窗口就收进库（复制，不动原文件）</summary>
        void EnableDropImport()
        {
            AllowDrop = true;
            DragOver += delegate(object s, DragEventArgs e)
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                    ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            Drop += delegate(object s, DragEventArgs e)
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return;
                e.Handled = true;
                SetStatus("正在导入 " + files.Length + " 项…（大文件要等一会儿）");
                string[] copy = files;
                ThreadPool.QueueUserWorkItem(delegate { app.ImportPaths(copy); });
            };
        }

        Border BuildCard(WallpaperItem it)
        {
            Border card = new Border();
            card.Width = 244;
            card.Height = 182;
            card.Margin = new Thickness(7);
            card.CornerRadius = new CornerRadius(10);
            card.Background = BgCard;
            card.BorderBrush = BorderIdle;
            card.BorderThickness = new Thickness(1);
            card.Cursor = Cursors.Hand;
            card.Tag = it;

            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(132) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Border imgHost = new Border();
            imgHost.CornerRadius = new CornerRadius(9, 9, 0, 0);
            imgHost.ClipToBounds = true;
            imgHost.Background = new SolidColorBrush(Color.FromRgb(34, 36, 44));

            string src = !string.IsNullOrEmpty(it.ImagePath) ? it.ImagePath : it.ThumbAbs;
            BitmapImage bi = LoadImage(src, 520);
            if (bi != null)
            {
                Image img = new Image();
                img.Source = bi;
                // 竖图别裁：UniformToFill 会把 9:16 的图裁成中间一条，
                // 卡片上就看不出这张壁纸长什么样了。竖图改成完整显示（两侧留暗底），
                // 横图维持满幅填充（比例基本一致，看不出边）。
                img.Stretch = bi.PixelHeight > bi.PixelWidth ? Stretch.Uniform : Stretch.UniformToFill;
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                imgHost.Child = img;
            }
            // 删除按钮：平时完全透明，鼠标移到卡片上才浮现，避免手滑点到
            Border del = new Border();
            del.Width = 24;
            del.Height = 24;
            del.CornerRadius = new CornerRadius(12);
            del.Background = new SolidColorBrush(Color.FromArgb(215, 24, 26, 32));
            del.BorderBrush = new SolidColorBrush(Color.FromArgb(150, 190, 90, 90));
            del.BorderThickness = new Thickness(1);
            del.HorizontalAlignment = HorizontalAlignment.Right;
            del.VerticalAlignment = VerticalAlignment.Top;
            del.Margin = new Thickness(0, 8, 8, 0);
            del.Cursor = Cursors.Hand;
            del.Opacity = 0;
            del.ToolTip = "从壁纸库删除（磁盘文件也会删掉）";

            TextBlock delX = new TextBlock();
            delX.Text = "×";
            delX.FontSize = 15;
            delX.FontWeight = FontWeights.Bold;
            delX.Foreground = new SolidColorBrush(Color.FromRgb(240, 200, 200));
            delX.HorizontalAlignment = HorizontalAlignment.Center;
            delX.VerticalAlignment = VerticalAlignment.Center;
            delX.Margin = new Thickness(0, -2, 0, 0);
            del.Child = delX;

            del.MouseLeftButtonUp += delegate(object s2, MouseButtonEventArgs e2)
            {
                e2.Handled = true;             // 别让这次点击冒泡成「设为壁纸」
                ConfirmDelete(it);
            };
            del.MouseEnter += delegate
            {
                del.Background = new SolidColorBrush(Color.FromArgb(235, 176, 58, 58));
            };
            del.MouseLeave += delegate
            {
                del.Background = new SolidColorBrush(Color.FromArgb(215, 24, 26, 32));
            };

            g.Children.Add(imgHost);

            // 左上角状态角标：一眼看出这张是「已经有飘动视频」还是「正铺在桌面上」。
            // v0.9.1：常驻创建、默认隐藏；文字/颜色由 RefreshBadges 统一重算，
            // 切换壁纸时只改这几个字，旧卡不会再挂着上一张的「播放中」。
            BadgeInfo info = new BadgeInfo();
            info.Item = it;
            info.HasVideo = app.HasCloudVideo(it);
            CloudGen.State cardState = CloudGen.StateOf(app.Config, it);
            info.Queued = !info.HasVideo && !it.IsVideo && cardState == CloudGen.State.Queued;
            // 11-14：没配 API 的工单绝不打「生成中」角标，那是假状态。
            info.Manual = !info.HasVideo && !it.IsVideo && cardState == CloudGen.State.Manual;
            // 11-15：WorkBuddy 积分通道的待领工单是真实状态，但绝不写成「生成中」
            info.BuddyPending = !info.HasVideo && !it.IsVideo
                                && cardState == CloudGen.State.BuddyPending;

            Border badge = new Border();
            badge.CornerRadius = new CornerRadius(0, 8, 8, 0);
            badge.Padding = new Thickness(8, 2, 8, 2);
            badge.HorizontalAlignment = HorizontalAlignment.Left;
            badge.VerticalAlignment = VerticalAlignment.Top;
            badge.Margin = new Thickness(0, 8, 0, 0);
            badge.Visibility = Visibility.Collapsed;
            badge.Tag = info;

            TextBlock bt = new TextBlock();
            bt.FontSize = 10.5;
            badge.Child = bt;
            stateBadges.Add(badge);
            g.Children.Add(badge);

            g.Children.Add(del);

            DockPanel bottom = new DockPanel();
            bottom.Margin = new Thickness(11, 7, 11, 7);
            bottom.LastChildFill = true;

            TextBlock tag = new TextBlock();
            tag.Text = TagNames.Cn(it.Tag);
            tag.FontSize = 11;
            tag.Foreground = Accent;
            tag.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(tag, Dock.Right);

            TextBlock name = new TextBlock();
            RegisterTitle(it.Title, name);
            name.FontSize = 13;
            name.Foreground = TextMain;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.VerticalAlignment = VerticalAlignment.Center;

            bottom.Children.Add(tag);
            bottom.Children.Add(name);
            Grid.SetRow(bottom, 1);
            g.Children.Add(bottom);

            card.Child = g;

            card.MouseEnter += delegate
            {
                card.Background = BgCardHover;
                del.Opacity = 1;
                if (currentItem == null || currentItem.Id != it.Id) card.BorderBrush = BorderHover;
            };
            card.MouseLeave += delegate
            {
                card.Background = BgCard;
                del.Opacity = 0;
                if (currentItem == null || currentItem.Id != it.Id) card.BorderBrush = BorderIdle;
            };
            card.MouseLeftButtonUp += delegate { app.ApplyItem(it); };
            card.ContextMenu = BuildMenu(it);

            // 把一段视频拖到这张图上 = 给它配飘动视频（零成本路线的主入口，最顺手）
            // 拖图片或拖一堆文件仍然走原来的「收进库」，两者靠扩展名区分。
            card.AllowDrop = true;
            card.DragOver += delegate(object s, DragEventArgs e)
            {
                e.Effects = (e.Data.GetDataPresent(DataFormats.FileDrop) && !it.IsVideo)
                    ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            card.Drop += delegate(object s, DragEventArgs e)
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return;
                e.Handled = true;
                string[] copy = files;
                SetStatus("正在导入 " + files.Length + " 项…（大文件要等一会儿）");
                ThreadPool.QueueUserWorkItem(delegate { app.ImportPaths(copy); });
            };

            if (currentItem != null && currentItem.Id == it.Id)
            {
                card.BorderBrush = BorderActive;
                card.BorderThickness = new Thickness(2);
            }

            return card;
        }

        /// <summary>
        /// 自检用：把这张图的右键菜单原样打进日志（含「动态播放」的子项）。
        /// 这台机器上前台焦点老被编辑器抢走，截图/点击都不可靠，直接读菜单对象最准。
        /// </summary>
        public void LogMenuFor(WallpaperItem it)
        {
            if (it == null) { Log.Note("自检 --menutest：条目为空"); return; }
            ContextMenu cm = BuildMenu(it);
            Log.Note("自检 --menutest：条目「" + it.Title + "」(" + it.Id + ")"
                     + " video=" + it.IsVideo + " motion=" + it.Motion
                     + " effect=" + it.Effect + " 有云端片=" + app.HasCloudVideo(it));
            for (int i = 0; i < cm.Items.Count; i++)
            {
                MenuItem mi = cm.Items[i] as MenuItem;
                if (mi == null) { Log.Note("自检 --menutest：  [分隔线]"); continue; }
                Log.Note("自检 --menutest：  - " + mi.Header);
                for (int k = 0; k < mi.Items.Count; k++)
                {
                    MenuItem sub = mi.Items[k] as MenuItem;
                    if (sub == null) { Log.Note("自检 --menutest：      [分隔线]"); continue; }
                    Log.Note("自检 --menutest：      · " + sub.Header
                             + (sub.IsCheckable ? "（可勾选，当前=" + sub.IsChecked + "）" : "")
                             + (sub.IsEnabled ? "" : "（灰）"));
                }
            }
        }

        /// <summary>
        /// 自检用：真去建一次右键菜单，确认静态图里确实有「复制交给 WorkBuddy 的指令」这一项。
        /// 只验文案存在，不触发点击，避免自检真去写剪贴板。
        /// </summary>
        public bool SelfTestHasBuddyMenuItem(WallpaperItem it)
        {
            if (it == null || it.IsVideo) return false;
            ContextMenu cm = BuildMenu(it);
            for (int i = 0; i < cm.Items.Count; i++)
            {
                MenuItem mi = cm.Items[i] as MenuItem;
                if (mi == null) continue;
                for (int k = 0; k < mi.Items.Count; k++)
                {
                    MenuItem sub = mi.Items[k] as MenuItem;
                    if (sub == null) continue;
                    string h = sub.Header == null ? "" : sub.Header.ToString();
                    if (h.IndexOf("WorkBuddy", StringComparison.Ordinal) >= 0) return true;
                }
            }
            return false;
        }

        ContextMenu BuildMenu(WallpaperItem it)
        {
            ContextMenu cm = new ContextMenu();

            MenuItem m0 = new MenuItem();
            m0.Header = "查看大图";
            m0.Click += delegate { ShowLocalPreview(it); };

            MenuItem m1 = new MenuItem();
            m1.Header = "设为壁纸";
            m1.Click += delegate { app.ApplyItem(it); };

            // 静态图的动态播放只有两项：云端生成（有片就是播放）、回退静态。
            // 场景动效、缓慢缩放、本地导入的入口都按主人要求删掉了，
            // 动态壁纸只走「云端生成的真视频」这一条路，不再有本机算法顶替。
            MenuItem mDyn = null;
            if (!it.IsVideo)
            {
                mDyn = new MenuItem();
                mDyn.Header = it.Motion ? "动态播放（当前：云端飘动视频）" : "动态播放";

                CloudGen.State cs = CloudGen.StateOf(app.Config, it);
                bool hasVid = app.HasCloudVideo(it);

                MenuItem mCloud = new MenuItem();
                MenuItem mRegen = null;
                if (hasVid)
                {
                    mCloud.Header = "云端动效：播放这段视频";
                    mCloud.IsCheckable = true;
                    mCloud.IsChecked = it.Motion && it.Effect == 2;
                    mCloud.Click += delegate { app.SetMotionFx(it, 2); };

                    // 11-14：有片不等于把「云端生成」入口删掉，给一个会先确认再花钱的重出通道。
                    mRegen = new MenuItem();
                    mRegen.Header = "✦ 重新生成一片（会重新计费）";
                    mRegen.ToolTip = "已经有片子了，只有想换一片时才点；会先弹确认说明费用与所需配置。";
                    mRegen.Click += delegate { app.RegenCloud(it); };
                }
                else if (cs == CloudGen.State.Manual)
                {
                    // 11-14：旧工单不会自动出片，别再让人点一次「生成」掉进假状态。
                    mCloud.Header = "云端生成：有一张不会自动出片的旧工单（点此查看）";
                    mCloud.Click += delegate { app.OpenJobFolder(it.Id); };
                }
                else if (cs == CloudGen.State.BuddyPending)
                {
                    // 11-15：待领工单还在，点一下重新拿指令（不会重复登记、不会重复扣积分）。
                    mCloud.Header = "云端生成：等 Buddy 出片（点此重新复制指令）";
                    mCloud.ToolTip = "WorkBuddy 积分通道：会话确认后才会生成 mp4，现在没有扣积分。";
                    mCloud.Click += delegate { app.StartBuddyGen(it); };
                }
                else
                {
                    mCloud.Header = cs == CloudGen.State.Queued ? "云端生成：生成中…"
                                  : cs == CloudGen.State.Failed ? "云端生成：上次失败，点此重试"
                                  : "云端生成动效视频（3 秒）";
                    mCloud.IsEnabled = cs != CloudGen.State.Queued;
                    mCloud.Click += delegate { app.GenCloud(it); };
                }

                MenuItem fxOff = new MenuItem();
                fxOff.Header = "回退静态";
                fxOff.IsEnabled = it.Motion;
                fxOff.Click += delegate { if (it.Motion) app.ToggleMotion(it); };

                mDyn.Items.Add(mCloud);
                if (mRegen != null) mDyn.Items.Add(mRegen);

                // 11-15：不填 Key 的替代出片路径。只复制一段话，绝不下单、不扣费。
                MenuItem mBuddy = new MenuItem();
                mBuddy.Header = CloudGen.BuddyMenuLabel;
                mBuddy.ToolTip = CloudGen.BuddyMenuTip;
                mBuddy.Click += delegate { app.CopyBuddyRequest(it); };
                mDyn.Items.Add(new Separator());
                mDyn.Items.Add(mBuddy);

                MenuItem mBuddyFolder = new MenuItem();
                mBuddyFolder.Header = "查看 WorkBuddy 待领工单";
                mBuddyFolder.Click += delegate { app.OpenBuddyFolder(); };
                mDyn.Items.Add(mBuddyFolder);
                mDyn.Items.Add(fxOff);
            }

            MenuItem m2 = new MenuItem();
            m2.Header = "下载 4K 封面";
            m2.Click += delegate { app.FetchCover(it); };

            MenuItem m3 = new MenuItem();
            m3.Header = "打开所在文件夹";
            m3.Click += delegate { OpenFolder(it); };

            cm.Items.Add(m0);
            cm.Items.Add(m1);
            if (mDyn != null) cm.Items.Add(mDyn);
            cm.Items.Add(m2);
            cm.Items.Add(m3);

            Separator sp = new Separator();
            cm.Items.Add(sp);

            MenuItem mDel = new MenuItem();
            mDel.Header = "删除这张（连文件一起删）";
            mDel.Foreground = new SolidColorBrush(Color.FromRgb(226, 132, 132));
            mDel.Click += delegate { ConfirmDelete(it); };
            cm.Items.Add(mDel);

            return cm;
        }

        /// <summary>删除确认：会删磁盘文件，必须先问一句</summary>
        void ConfirmDelete(WallpaperItem it)
        {
            if (it == null) return;
            string extra = it.IsVideo ? "\r\n\r\n这是动态壁纸视频，删除后无法恢复。" : "";
            MessageBoxResult r = MessageBox.Show(
                "从壁纸库删除「" + DisplayTitle(it.Title) + "」？" + extra +
                "\r\n\r\n磁盘上的文件会一起删除，不能撤销。",
                "删除壁纸",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (r != MessageBoxResult.OK) return;
            app.DeleteItem(it);
        }

        void OpenFolder(WallpaperItem it)
        {
            try
            {
                string p = !string.IsNullOrEmpty(it.ImagePath) && File.Exists(it.ImagePath) ? it.ImagePath : it.ThumbAbs;
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    Process.Start("explorer.exe", "/select,\"" + p + "\"");
                else
                    Process.Start("explorer.exe", app.Config.LibRoot);
            }
            catch (Exception ex) { SetStatus("打开文件夹失败：" + ex.Message); }
        }

        // ---------------- 图片解码缓存 ----------------

        /// <summary>
        /// 卡片缩略图的解码缓存（LRU）。
        ///
        /// 以前每次 RefreshList 都把整个库的图重新从磁盘解码一遍：LoadImage 用的是
        /// OnLoad + IgnoreImageCache，等于完全没有缓存。19 张时只是「刷新慢一点」，
        /// 库到几百张、或者每次换台都重刷时就会明显卡。
        /// 键里带 LastWriteTime，图被重新下载/替换后自然失效，不会显示旧图。
        /// </summary>
        static readonly Dictionary<string, BitmapImage> ImageCache =
            new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);
        static readonly List<string> ImageCacheOrder = new List<string>();
        const int ImageCacheMax = 160;

        static BitmapImage LoadImage(string path, int decodeWidth)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            string key;
            try { key = path + "|" + decodeWidth + "|" + File.GetLastWriteTimeUtc(path).Ticks; }
            catch { key = path + "|" + decodeWidth; }

            lock (ImageCache)
            {
                BitmapImage hit;
                if (ImageCache.TryGetValue(key, out hit))
                {
                    ImageCacheOrder.Remove(key);
                    ImageCacheOrder.Add(key);       // 命中就挪到队尾，最久没用的才会被淘汰
                    return hit;
                }
            }

            BitmapImage bi = DecodeImage(path, decodeWidth);
            if (bi == null) return null;

            lock (ImageCache)
            {
                if (!ImageCache.ContainsKey(key))
                {
                    ImageCache[key] = bi;
                    ImageCacheOrder.Add(key);
                    while (ImageCacheOrder.Count > ImageCacheMax)
                    {
                        string old = ImageCacheOrder[0];
                        ImageCacheOrder.RemoveAt(0);
                        ImageCache.Remove(old);
                    }
                }
            }
            return bi;
        }

        static BitmapImage DecodeImage(string path, int decodeWidth)
        {
            try
            {
                BitmapImage bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                if (decodeWidth > 0) bi.DecodePixelWidth = decodeWidth;
                bi.UriSource = new Uri(path, UriKind.Absolute);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        public void SetStatus(string text)
        {
            statusLeft.Text = text;
        }

        /// <summary>按当前真实状态刷一张角标。alive 由 RefreshBadges 算一次传进来，
        /// 别每张卡都去枚举进程，也别在这里碰磁盘/画面指纹（看门狗每 4 秒会调一次）。</summary>
        void ApplyBadge(Border badge, bool alive)
        {
            BadgeInfo info = badge.Tag as BadgeInfo;
            TextBlock bt = badge.Child as TextBlock;
            if (info == null || bt == null) return;

            bool playing = app.IsPlaying(info.Item, alive);
            bool queued = info.Queued;
            bool manual = info.Manual;
            bool buddy = info.BuddyPending;

            if (!info.HasVideo && !playing && !queued && !manual && !buddy)
            {
                badge.Visibility = Visibility.Collapsed;
                bt.Text = "";
                return;
            }

            badge.Visibility = Visibility.Visible;
            badge.Background = playing
                ? new SolidColorBrush(Color.FromArgb(228, 46, 92, 168))
                : queued
                    ? new SolidColorBrush(Color.FromArgb(220, 40, 78, 84))
                    : buddy
                        ? new SolidColorBrush(Color.FromArgb(220, 118, 84, 40))
                        : manual
                            ? new SolidColorBrush(Color.FromArgb(220, 96, 70, 34))
                            : new SolidColorBrush(Color.FromArgb(216, 56, 46, 82));

            bt.Text = playing ? "▶ 播放中"
                    : queued ? "☁ 生成中"
                    : buddy ? "⏳ 等 Buddy 出片"
                    : manual ? "⏸ 待处理" : "✦ 飘动";
            bt.Foreground = playing ? Brushes.White
                                    : queued ? new SolidColorBrush(Color.FromRgb(168, 232, 226))
                                    : buddy ? new SolidColorBrush(Color.FromRgb(255, 214, 158))
                                    : manual ? new SolidColorBrush(Color.FromRgb(240, 206, 146))
                                             : new SolidColorBrush(Color.FromRgb(228, 217, 255));
        }

        /// <summary>
        /// 状态一变就调这个：只改角标文字/颜色，不重建卡片、不闪图。
        /// 「播放中」除了看配置，还要求播放器进程真的活着 —— 配置万一残留，
        /// 进程已经没了也绝不显示假的播放中。
        /// </summary>
        public void RefreshBadges()
        {
            bool alive = app.PlayingNow();
            for (int i = 0; i < stateBadges.Count; i++) ApplyBadge(stateBadges[i], alive);
        }

        /// <summary>自检用：读某张卡当前的角标文字。null = 没这张卡，"" = 角标隐藏。</summary>
        public string SelfTestBadgeText(string id)
        {
            for (int i = 0; i < stateBadges.Count; i++)
            {
                BadgeInfo info = stateBadges[i].Tag as BadgeInfo;
                if (info == null || info.Item == null || info.Item.Id != id) continue;
                if (stateBadges[i].Visibility != Visibility.Visible) return "";
                TextBlock t = stateBadges[i].Child as TextBlock;
                return t == null ? "" : t.Text;
            }
            return null;
        }

        /// <summary>自检用：读状态栏右侧「当前：xxx」。</summary>
        public string SelfTestStatusRight()
        {
            return statusRight.Text;
        }

        public void UpdateStatusRight()
        {
            string cur = currentItem == null ? "未设置" : DisplayTitle(currentItem.Title);
            statusRight.Text = string.Format("当前：{0}   轮换：每 {1} 分钟", cur, app.Config.IntervalMinutes);
        }

        /// <summary>大图预览是否正在全屏显示（它也占满屏幕，全屏检测要跳过它）</summary>
        public bool PreviewShowing
        {
            get { return overlay != null && overlay.Visibility == Visibility.Visible; }
        }

        public void SetCurrent(WallpaperItem it)
        {
            currentItem = it;
            UpdateStatusRight();
            HighlightCurrent();
            RefreshBadges();        // 「当前」变了，播放中角标也要跟着走
        }

        void HighlightCurrent()
        {
            for (int i = 0; i < cards.Children.Count; i++)
            {
                Border card = cards.Children[i] as Border;
                if (card == null) continue;
                WallpaperItem it = card.Tag as WallpaperItem;
                if (it == null) continue;
                bool active = currentItem != null && it.Id == currentItem.Id;
                card.BorderBrush = active ? BorderActive : BorderIdle;
                card.BorderThickness = new Thickness(active ? 2 : 1);
            }
        }

        void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!app.ReallyExit)
            {
                e.Cancel = true;
                Hide();
                app.NotifyMinimized();
            }
        }
    }
}
