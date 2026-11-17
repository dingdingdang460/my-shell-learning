# 壁纸管家 Wallpaper Manager

一个跑在 Windows 桌面上的壁纸管理程序：本地图库 + 联网进货 + 静态/动态（视频）壁纸播放 + 定时与时段轮换。
动态壁纸由云端生成短视频（≤3 秒），出片后自动做合规角标验收与局部修补，再换上桌面。

- 当前版本：**v0.9.12**
- 技术栈：**C# 5 + WPF + .NET Framework 4.8**；播放器用 Media Foundation 解码、GDI 绘制
- 构建：**零下载**——直接用 Windows 自带的 `csc.exe`，不需要 Visual Studio / Rust / NuGet
- 远端备份：`https://github.com/dingdingdang460/my-shell-learning`（本仓库）

> 早期 `docs/REQUIREMENTS.md` 里的 Tauri/React 方案已作废，实际问题以本文件和 `docs/PROGRESS.md` 为准。

---

## 主要功能

### 壁纸库与浏览
- 扫描本地库（`index.json` + `images/`、`videos/`、`thumbs/`），卡片网格展示缩略图、标题、标签、体积、时长、分辨率
- 关键词搜索（标题/分类/拼音首字母）、大图预览翻页、筛选条按类型（全部/视频/静态/动效）与分类筛选
- 竖图与异形比例自动适配（模糊底衬合成、EXIF 方向），不会被丢弃
- 拖拽 / 批量导入本地图片，自动按分辨率分横竖屏

### 联网进货
- motionbgs：站内搜索、分类浏览、1080P mp4、4K 静态封面
- B 站：视频搜索与专栏图集解析（多路兜底）
- 中文关键词自动走图集、英文走壁纸站；下载落盘后写索引直接进库
- 4K 逐张升级：下载后读真实分辨率，**宽度 ≥3000 才换片**（不按文件大小判断）

### 上屏与播放
- 静态壁纸：`SystemParametersInfoW` 一键设置
- 动态壁纸：桌面层挂载（分层子窗口 + GDI 直画），桌面图标可点、不被遮挡
- 全屏游戏/放映自动暂停，退出恢复；解锁登录后自动续播
- 播放器看门狗 4 秒一轮、连续 2 轮缺失才重启，避免误判；换片无黑场
- 电池供电降级、电量低于阈值停动态（阈值可调）

### 动态壁纸与出片
- 两种出片通道：**WorkBuddy 积分工单**（默认）或**服务商 API Key 直连**，详见下文
- 相关视频七层发现 + 四道下单闸：已有片子一律复用，不重复花钱
- 画面指纹判定（MF 抽首帧 + 灰度 MAD + dHash 双阈值），防止把别的壁纸的片播到新图上
- 出片后抽帧检测左上角合规角标，ffmpeg `delogo` 局部修补，复查干净才交付
- 播放状态统一收口：切换/停止/失败/删除都实时更新角标，不出现"切了图还显示播放中"

### 轮换与便利功能
- 定时轮换（分钟级间隔）
- **时段轮换 v0.9.12**：可配置时段表 + 壁纸分组，例如
  `早晨|06:00-09:00;白天|09:00-18:00;夜晚|18:00-06:00`（跨零点写成"起 > 止"）；
  不分组的图任何时段都参与，命中组没图则退回全库，轮换不会卡死
- F5 回滚上一张、全局快捷键（`Ctrl+Alt+Space` 暂停/继续 / `Ctrl+Alt+N` 下一张，窗口不在前台也能用）
- 欢迎动画（登录/解锁触发，按壁纸分类取文案，点击或 Esc 跳过）
- 托盘菜单：当前壁纸名、换一张、重新应用、动态壁纸控制、设置、退出

### 删除与还原（安全）
- 删除连相关视频一起删；一张图对应一条回收站记录，缩略图等派生文件不单独进回收站
- 送回收站后会核验 `$Recycle.Bin` 里的 `$I` 记录：未确认进回收站就保留 `.trash` 副本，不会误报"可还原"
- 取消云端订单不会清掉动效标记

---

## 快速开始

### 环境
- Windows + .NET Framework 4.8（ Win10/11 自带运行时与 `csc.exe`）
- 动态壁纸播放需要 Media Foundation（系统自带）；自动去水印需要本机有 ffmpeg（见下）

### 构建
在仓库根目录双击或运行：

```bat
build.bat
```

产物：`app\bin\WallpaperManager.exe`（主程序）与 `app\bin\WallpaperPlayer.exe`（桌面播放器）。
两个 exe 分开编译：播放器崩溃不会拖垮主程序。`build.log` / `build-player.log` 为 0 字节即零错误零警告。

> 想只编译不启动 GUI：`build.bat --noboot`。

### 运行
直接双击 `app\bin\WallpaperManager.exe`。首次运行会在数据目录生成 `config.json`，默认壁纸库根目录是 `D:\Wallpaper`（可在设置里改）。

### 打包
```bat
package.bat
```
生成 `dist\WallpaperManager-vX.Y\`（当前 v0.9.12）。

---

## 目录结构

| 路径 | 说明 |
| --- | --- |
| `app/*.cs` | 全部源码（36 个文件，`build.bat` 里显式列出） |
| `app/bin/` | 编译产物（exe，已 gitignore） |
| `build.bat` / `package.bat` | 编译 / 打包脚本 |
| `docs/PROGRESS.md` | 开发进度与技术决策总账，**了解现状首选** |
| `docs/交接-*.md`、`docs/功能盘点-*.md` | 阶段性交接与功能盘点 |
| `docs/REQUIREMENTS.md` | 早期需求，**技术方案已作废**，仅作历史追溯 |
| `tools/` | 构建检查、出片、抓帧等辅助脚本 |
| `resources/` | 图标等资源 |
| `push-backup.bat` | 源码备份到 GitHub 的一键脚本（见下） |
| `.gitignore` | 排除 exe/zip/配置/令牌等；clone 后体积约 2MB（原始约 678MB） |

源码里几个关键文件：`Program.cs`（主逻辑/轮换/自检分派）、`MainWindow.cs`（界面与右键菜单）、
`LibraryStore.cs`（索引与配置读写）、`TimeSlots.cs`（时段判定）、`RelatedVideo.cs`（片源发现）、
`Watermark.cs`（角标验收）、`SelfTestExtra.cs`（自检实现）。

---

## 配置

数据目录默认是 `%APPDATA%\wallpaper`，可通过 `app\bin\data-root.txt` 迁到任意已有目录（一行绝对路径），
本机已迁到仓库内的 `appdata/`（已 gitignore，**可能含 API Key，绝不入库**）。

主要配置项（在设置页修改，或看 `config.json`）：

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `LibRoot` | `D:\Wallpaper` | 壁纸库根目录 |
| `IntervalMinutes` / `AutoRotate` | 180 / 开 | 定时轮换间隔与开关 |
| `TimeRotateEnabled` / `TimeSlots` | 关 / 三组 | 时段轮换开关与时段表 |
| `DynamicEnabled` / `DynamicFps` | 关 / 30 | 动态壁纸总开关与帧率上限 |
| `MountMode` | 8 | 桌面挂载层级（8 = 分层子窗口，实测可上屏且图标可见） |
| `GenChannel` | `buddy` | 出片通道：`buddy` 积分工单 / `api` 服务商 Key 直连 |
| `GenSeconds` / `GenResolution` | 3 / 720P | 出片时长（硬上限 3 秒）与分辨率 |
| `GenProvider` / `GenApiKey` / `GenModel` | 空 | 服务商 API Key 直连配置（明文存本机，界面不回显） |
| `PauseOnBattery` / `BatteryPausePercent` | 开 / 20 | 电池降级阈值 |
| `FfmpegPath` / `AutoDelogo` | 空 / 开 | ffmpeg 路径与出片后自动去角标 |
| `Prefer4k` | 关 | 逐张升级 4K（本机 1080P 无收益，右键单张升级） |

API Key 只存在本机的 `config.json`，程序不会把它发往除所选服务商以外的任何地方。

---

## 动态壁纸：两条出片通道（重要边界）

1. **WorkBuddy 积分工单（默认，`GenChannel=buddy`）**
   点"一键生成"后检测客户端是否运行 → 登记待领工单 → 由 WorkBuddy 会话领单执行出片 → 到货自动换上桌面。
   **必须开着 WorkBuddy 客户端**，有每小时定时任务扫 `jobs\buddy\` 并领单出片。
   注意：WorkBuddy 的对话模型（Hy4/Hy3 等）**不能生成 mp4**，出片走多模态模型消耗积分。

2. **服务商 API Key 直连（`GenChannel=api`）**
   切到 `api` 并填自己的百炼/方舟 Key 后按秒付费直连（720P 约 ¥0.45 / 3 秒）。
   这是**完全不打开 WorkBuddy 客户端也能出片**的唯一路径。

程序自己**不会**代填 Key、不会对服务商发起验证请求、不会产生任何费用；涉及花钱的验收由主人自己决定时机。

### 去水印口径
平台按《标识办法》在左上角打「AI生成」合规角标，提示词去不掉。因此流程是：
**先生成 → 出片后抽 3 帧检测（1.0/0.4/2.2 秒）→ ffmpeg `delogo` 局部修补 → 复查干净才交付**。
框按比例计算，不写死像素；修补前先停播放器（否则 mp4 被占句柄）；每个片子的备份 `.bak-wm` 不覆盖。
没检测到 ffmpeg 时报"未验收"，不会说成"干净"；有角标且没修干净的片不换上。

---

## 自检

程序带大量命令行自检，全部隔离运行（不改真实壁纸库/配置）。跑之前先关掉主程序，并把 `TEMP`/`TMP` 分两句指向仓库内 `.selftest-tmp/`：

```bash
export TEMP="$(pwd -W)/.selftest-tmp"
export TMP="$(pwd -W)/.selftest-tmp"
./app/bin/WallpaperManager.exe --timeslottest
```

常用项：

| 开关 | 覆盖 |
| --- | --- |
| `--timeslottest` | 时段轮换（v0.9.12，19 条） |
| `--guardtest` / `--uhdtest` / `--contenttest` | 看门狗 / 4K / 图片视频指纹不串片 |
| `--badgetest` / `--switchmotiontest` / `--watermarktest` | 角标 / 切换动效 / 水印验收 |
| `--buddyhinttest` / `--autogentest` | 积分工单链与一键生成 |
| `--deltest` / `--importexporttest` / `--configtest` | 删除还原 / 导入导出 / 配置 |
| `--fixlogo` / `--fixlogo --dry` | 出片后修补角标（批处理） |

判 PASS 的规则是 detail 里不含 `×`，所以断言文本里不写乘号。
**`--cloudtest` 不要跑**——它会往真实队列写死工单。
带 `--*test` 的进程会跳过壁纸恢复和看门狗，不会污染下一次自检。

---

## 备份源码到 GitHub

本仓库只收源码、文档、工具脚本与资源；`config.json`、`appdata/`、`dist/`、`*.exe`、`pat.txt` 等已被 `.gitignore` 排除。

仓库里有一键脚本 `push-backup.bat`。因为本机 GitHub 浏览器登录被 IPv6 回跳问题挡住，脚本改用 PAT 直连：
1. 到 `https://github.com/settings/tokens/new` 生成令牌（Note 随意，选 90 天，公开仓库勾 `public_repo` 即可）；
2. 双击 `push-backup.bat`，它会自动打开 `pat.txt`，把令牌粘进去保存、关闭记事本、回窗口按回车；
3. 脚本读取令牌 → 推送 → **立即删除 `pat.txt`** → 核对远端提交号。

令牌只在内存中用于本次推送，`pat.txt` 已加入忽略规则，不会残留或入库。

---

## 已知限制 / 未做

- **多屏**：主屏假设写死，多显示器未适配（需求已定不做）
- **远程备份**：脚本需主人提供一次 PAT；连接器的 token 只读，无法代写
- **B 站图集 4K 原图**：需登录，匿名上限 1080P
- **循环首尾跳帧**：暂无交叉淡化（ping-pong），当前靠首帧对齐减轻
- **4K / HDR 真机验证**：本机只有 1080p 单屏，未做实机验证
- 未做：断点续传、yt-dlp 侧车、锁屏同步、时段轮换以外的复杂调度

---

## 文档索引

| 文档 | 内容 |
| --- | --- |
| `docs/PROGRESS.md` | **首选**：全部技术决策、版本演进、事故复盘 |
| `docs/交接-2026-11-16.md` | 最近一次交接，含剩余功能甄别 |
| `docs/功能盘点-2026-11-12.md` | 早期功能盘点与优先级建议 |
| `docs/REQUIREMENTS.md` | 早期需求（Tauri/React 方案已作废，仅作历史） |
