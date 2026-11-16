using System;

namespace WallpaperManager
{
    /// <summary>壁纸条目：一条 = 一个本地壁纸（动态视频或静态图片）</summary>
    public class WallpaperItem
    {
        public string Id = "";
        public string Title = "";
        public string Tag = "";
        public string File = "";        // 相对库根，例如 videos/xxx.mp4
        public string Thumb = "";       // 相对库根，例如 thumbs/xxx.jpg
        public long Bytes;
        public double Duration;
        public string Upgrade4k = "";
        public string ThumbAbs = "";    // 缩略图绝对路径
        public string ImagePath;        // 已就绪的静态图（4K 封面或库内图片），null 表示未下载
        public bool IsVideo;
        public bool Motion;             // 静态图也当动态壁纸播，由用户手动开
        public bool Fav;                // 收藏：只在「只看收藏」视图里出现，排序也优先
        public int Effect;              // 动效方式：2 = 云端飘动视频（videos\<id>.mp4）；0 = 静态（1 是老编号，已废）
    }

    /// <summary>用户配置，存 %APPDATA%\wallpaper\config.json</summary>
    public class AppConfig
    {
        public string LibRoot = @"D:\Wallpaper";
        public int IntervalMinutes = 180;
        public bool AutoRotate = true;
        public string LastId = "";

        // v0.3 新增
        public bool AutoStart;                 // 开机自启（写 HKCU Run）
        public bool WelcomeOnBoot = true;      // 开机后播欢迎
        public bool WelcomeOnUnlock = true;    // 每次解锁播欢迎
        public bool FilterPortrait;            // 竖版图不入库（默认关：竖图现在会自动适配屏幕，不该再丢）
        public int DynamicFps = 30;            // 动态壁纸帧率上限
        public string LastDynamicId = "";      // 上次的动态壁纸，下次启动接着播
        public bool DynamicEnabled;            // 动态壁纸总开关
        public int MountMode = 8;               // 挂载层级：8=Progman分层子窗口·DefView之下(官方口径，能上屏+图标可见)；0-7 均实测否决

        // v0.5 云端动效
        public int GenSeconds = 3;              // 出片秒数，硬上限 3（多花额度没意义，循环够用）
        // 正在等云端出片的那一条：片子到货后自动换上，重启也不会忘（存在 config 里而不是内存里）
        public string PendingCloudId = "";

        // ---- 云端直连（GenApi.cs）。填了就能在应用里直接出片，不用外部工具 ----
        // "wanx" = 通义万相（便宜）、"ark" = 火山方舟（效果好）、空 = 不用 API
        public string GenProvider = "";
        public string GenApiKey = "";           // 明文存本机 config.json，界面上只提示、不回显
        public string GenModel = "";            // 留空就用该服务商的默认模型
        public string GenResolution = "720P";   // 720P / 1080P

        // 11-15：出片通道。"buddy" = 优先消耗 WorkBuddy 积分（默认，不用填 Key），
        // "api" = 走服务商 API Key 直连（按秒付费）。老配置没这个字段时 Load 会回填 buddy。
        public string GenChannel = "buddy";

        // 11-15：生成前要检测 WorkBuddy 客户端可用；不可用时「申请启动」用这个路径。
        // 留空 = 自动发现（运行进程 / 常见安装位置 / 开始菜单快捷方式），
        // 便携安装到非常规目录时可以在设置里手填 WorkBuddy.exe 的完整路径。
        public string BuddyExe = "";

        // ---- 网络（Net.cs）----
        // 空 = 直连（以前的行为）；填 host:port 或 http://host:port 就走代理。
        public string Proxy = "";

        // ---- 缓存 ----
        // 自适应合成图（竖图补底衬、异形比例）的磁盘上限，单位 MB，超了删最旧的。
        public int CacheLimitMb = 300;

        // ---- 功耗守卫（11-16）----
        // 笔记本电池供电且电量不高于阈值时暂停动态壁纸解码，插电或电量回升自动恢复。
        // 桌面机没有电池，GetSystemPowerStatus 报「交流供电」，这里永远不会触发。
        // 老配置没有这两个键时：守卫默认开、阈值默认 20%。
        public bool PauseOnBattery = true;
        public int BatteryPausePercent = 20;

        // ---- 角标验收（11-16）----
        // 出片后抽帧检查左上角「AI生成」合规角标，检测到就 delogo 修补，修干净才入库。
        // 依赖本机 ffmpeg（引用不外带）。FfmpegPath 留空 = 自动发现（程序目录 / PATH /
        // WorkBuddy 自带 Python 的 imageio-ffmpeg），便携环境或装了别的版本时可手填。
        public string FfmpegPath = "";
        public bool AutoDelogo = true;
    }
}
