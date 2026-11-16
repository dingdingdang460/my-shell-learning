# 进度与待办 · 2026-11-11 / 11-12

> 给下一个自己看：先读「今天改了什么」→「需要主人验证的 3 件事」→「动态壁纸卡在哪」。
> 11-12 新增第十节「人物图局部运动」，那是目前最新的一块。

---

## 一、环境与构建（没变，别再折腾）

| 项 | 状态 |
|---|---|
| 技术栈 | C# 5 + WPF + .NET Framework 4.8（net48），**零下载** |
| 编译器 | `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` |
| 构建入口 | 双击 `build.bat` → 同时产出 `app\bin\WallpaperManager.exe` 和 `app\bin\WallpaperPlayer.exe` |
| 日志 | `build.log`（管理器）、`build-player.log`（播放器）；0 字节 = 无错无警告 |
| 打包 | 双击 `package.bat` → `dist\WallpaperManager-v0.3\` + `.zip` |
| 运行期日志 | `%TEMP%\wallpaper-debug.log`（`Log.Note`，512KB 自动重来） |
| 壁纸库 | `D:\Wallpaper`（可在设置里改） |

**写代码必须遵守**：
1. 源码 **UTF-8（BOM 可有可无，bat 里已带 `/codepage:65001`）**。
2. 只能 C# 5：禁 `$""`、`?.`、`nameof`、表达式体成员、`using static`。
3. 沙箱里 `csc.exe` 被硬禁，**编译只能主人双击 build.bat**；所以每次交付前要靠读代码 + 括号平衡自检。
4. bat 里不要 `timeout`（重定向下秒返回），用 `ping -n 2 127.0.0.1`。
5. **bat 里别写中文文件名/中文 echo**：cmd 按 ANSI 读，UTF-8 中文会乱码。

---

## 二、今天（11-11）改了什么

### 1. 卡死级 bug：专栏图集读不出来 —— 已按「多路兜底 + 错误可见」重写

**先纠正昨天的猜测**：`curl https://api.bilibili.com/x/article/view?id=10870988` 实测 `code:0`，`content` 里**有 26 张 `<img>`**，`template_id=4` 也没问题。所以「opus 型专栏 content 为空」不成立。

真正的原因有两层，昨天都没法看见：

| 问题 | 昨天的样子 | 今天 |
|---|---|---|
| 请求无超时 | `WebClient.DownloadString` 默认 100 秒，挂住就是纯等待 | 换 `HttpWebRequest`：`Timeout=12000`、`ReadWriteTimeout=15000`、gzip 自动解压、`CookieContainer` 装 buvid3 |
| 失败信息被遮住 | 只写状态栏，而状态栏在 overlay 下面 | 错误直接画在预览面板里 + 写 `%TEMP%\wallpaper-debug.log` |

`Bili.LoadArticleImages` 现在五路兜底，每一路都单独记日志：
1. `content` 的 `<img>` 标签（顺手取 `width/height`）
2. `data.opus.content.paragraphs[].pic.pics[]`（**实测 opus 型专栏图片在这里，且带宽高**）
3. `data.image_urls` / `origin_image_urls`
4. 抓 `https://www.bilibili.com/read/cv<id>` 网页 HTML（先按标签、再按 `/bfs/article/` 裸 URL 正则）
5. 全失败就用列表页那 1-3 张封面，并在面板写明「只能拿到 N 张」

### 2. 预览层：错误/重试/取消，重复按钮没了

- 新面板 `ShowArticleLoading`：标题 + 完整错误原因 + 原帖地址 + `重试 / 在浏览器打开 / 取消` 三个出口。
- 去掉了加载态里重复的「在浏览器打开」（之前 `PrimaryText` 和 `PageUrl` 各塞了一个）。
- 加了 `loadToken`：关掉预览（Esc / 取消）后，还在路上的请求结果直接丢弃，不会突然弹回来。
- `DownloadArticleImage` 的下载也带超时了（`Bili.DownloadTo` 换 `HttpWebRequest` 流式写盘）。

### 3. 竖版图过滤（主人选的：默认过滤）

- `BiliArticle.Sizes`：与 `Images` 一一对应的 `"1754x1240"`。
- `MainWindow.FilterPortrait`：`h > w` 的直接丢掉，图集 meta 显示「已滤掉 N 张竖版」。
- 设置里可关（`filterPortrait`），关掉后照收。
- 顺带好处：预览 bottom bar 会显示当前这张的真实分辨率。

### 4. 欢迎动画：开机 + 每次解锁都播

- `App.HookSession()`：`WTSRegisterSessionNotification(hwnd, 0)` + `HwndSource.AddHook`。
- 消息 `WM_WTSSESSION_CHANGE(0x02B1)`，`wParam == WTS_SESSION_UNLOCK(0x8)` → 延迟 450ms 播。
- 开机后 1.2 秒播一次（`welcomeOnBoot`）。
- 窗口 `ShowActivated = false`，不抢解锁后的焦点；退出时 `WTSUnRegisterSessionNotification`。

### 5. v0.3：设置面板 / 托盘 / 图标 / 打包

- `SettingsWindow`（`app/Settings.cs`）：库路径（可浏览）、轮换间隔与开关、开机自启、开机/解锁欢迎、竖版过滤、动态壁纸帧率。
- 开机自启写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\WallpaperManager`，不需要管理员。
- 托盘菜单补齐：打开主界面 / 换一张 / 暂停定时轮换 / 动态壁纸（暂停·继续·停止·探测）/ 预览欢迎 / 设置 / 退出。
- 图标：`tools/make_icon.py` 用 Pillow 手绘（深色圆角方块 + 叠放的照片卡 + 太阳和山），产出 `resources/app.ico`（16→256 七档）与 `app-256.png`；exe 用 `/win32icon` 内嵌，托盘读 `resources/app.ico`。
- `package.bat` + `docs/readme.txt`：免安装目录 + zip。

---

## 三、需要主人验证的 3 件事

1. **图集**：点一篇 B站图集 → 应直接进图集；若失败，面板里会写清原因（不再是干等）。日志在 `%TEMP%\wallpaper-debug.log`。
2. **欢迎动画**：设置里勾着「开机后播」和「解锁播」→ 锁屏再解锁，应该看到淡入淡出。
3. **动态壁纸**（先跑 1，再跑 2）：
   - **探测**：托盘 → 动态壁纸 → 跑一次可见性探测。日志按 `[1]` 环境 / `[3]` 自检 / `[4]` M1 / `[5]` M2 分段，
     最后一行是 `结论：VISIBLE` 或 `NOT_VISIBLE`。会闪 1～3 次红屏（那是自检和每组的洋红窗口），属正常。
   - **播放**：本地视频卡片 → 预览 → 「播放为动态壁纸」。真正把 mp4 播进桌面层的就是这条。
   - 两边的明细都落在 `%TEMP%\wallpaper-debug.log`，弹窗只显示「最近一次运行」那一段。

---

## 四、动态壁纸：已跑通（mountMode 8）

### 12:22 定案

**唯一可用的档位是 mountMode 8**：`Progman` 的 `WS_EX_LAYERED` 子窗口 +
`SetLayeredWindowAttributes(alpha=255)` + **GDI 直画**（`StretchDIBits`），
Z 序卡在 `SHELLDLL_DefView` 之下、`WorkerW` 之上。

实测 Z 序 `[0]DefView [1]WpPlayerWnd [2]WorkerW`，画面 29~30fps 铺满桌面，
桌面图标全部可见可用。默认档位已在 `Models.AppConfig.MountMode` /
`Dynamic.MountMode` / `Program.Initialize` 统一到 8（老配置自动升级）。

一整轮 0~7 的实测否决记录：

| 档位 | 做法 | 结果 |
|---|---|---|
| 0/1/2/3 | Progman 子窗口 + `UpdateLayeredWindow` | ULW 提交成功、满帧，屏幕 **0 像素** |
| 4 | 先置顶再压 DefView | 同样 0 像素 |
| 5 | 顶层窗口 `SetWindowPos` 到 Progman 之后 | **能上屏**，但落在 Progman 之上，图标全被盖住 |
| 6 | 顶层窗口 `HWND_BOTTOM` | 沉到桌面之下，完全不合成 |
| 7 | `SetWindowBand(ZBID_DESKTOP)` | 被拒，err=5 |

> 一句话记住根因：**`UpdateLayeredWindow` 把整个表面交给 DWM，在 NOREDIRECTIONBITMAP
> 的 Progman 下面完全不被合成；`SetLayeredWindowAttributes` 保留窗口自己的重定向表面，
> GDI 才画得进去。**

另一个坑（不是渲染问题）：`--dynvideo` 启动 1.2s 后欢迎动画全屏弹出盖住桌面，
全屏检测顺手把动态壁纸暂停 —— 验证时必须跳过欢迎动画。

### 15:20 修掉两个使用中暴露的问题

**1. 屏幕顶部 16px 纯绿 `(0,230,0)`** —— 不是挂载、不是绘制，是 **NV12 转换的平面偏移**：

- MF 报的帧高是 1080，但解码器给的 buffer 高度按 16 对齐成 **1088**；
- UV 平面真正起点在 `stride × 1088`，代码按 `stride × 1080` 算，顶部 16 行读到
  Y 平面后面的填充字节（全 0）；
- `U = V = 0` 走 BT.709 正好算出 `(0,230,0)`，于是天生长着一条绿边。

定位手段值得复用：`BufferPixel()`（读帧缓冲）和 `WindowPixel()`（读窗口 DC）一起打——
两边一致就说明**画面本身就是错的**，可以立刻排除合成/绘制方向。

修法：用 buffer 总长度反推真实对齐高度
`alignedH = len × 2 / (3 × stride)`，落在 `[h, h+128]` 才采纳，否则按 16 对齐兜底。
**高度不整除 16 的视频（1080p 全部中招）都会修复**，不是只修这一个文件。

另外 Y 平面越界判断改用 `len`，不能用 `need`（`need` 只算到 UV 末尾）。

**2. 换一张图片之后没有动态背景** —— 两处：

- `ApplyItem` 原来不分视频/图片，视频项也走 `DoSet`（把封面 jpg 设成壁纸），
  所以"点了视频却没有动态壁纸"。现在：视频项 → `StartDynamic`；
  静态图 → 先 `StopDynamic(false)` 再设图（否则视频会盖着刚换的图）。
- 播放器会因换壁纸 / Explorer 重绘 / 自己崩而掉线。新增**看门狗**
  （`Program.StartWatchdog`，4 秒一轮，首检延迟 8 秒）：进程或桌面层窗口不在，
  就按 `LastDynamicId` 自动重启；连续 5 次失败则置 `DynamicEnabled=false` 并提示，
  不会无限刷。实测杀掉播放器后 **3 秒内自动拉回**（PID 18816 → 17052）。

顺带修掉：`SetMountMode` 里 `if (m > 4) return;` 把默认档 8 挡在门外（托盘「桌面层级」
点任何一项都没反应）；`StartFirstVideo` 与 `RestoreDynamic` 各起一次播放器导致闪黑两次。

自测工具：
- `python tools/shot.py <mount>`：启动→抓屏→与基线逐像素比对→收尾。
- `python tools/diag.py [mount]`：窗口矩形/客户区/Z 序 + 顶部像素 + 换壁纸后复查。
- `python tools/final.py [秒]`：一条命令跑完「启动管理器 → 等 → 最小化前台 → 抓屏 → 收尾」。
- `python tools/wd2.py`：按 PID 杀播放器验证看门狗。
- `python tools/loop_test.py [秒]`：连续观察，数循环次数与"进程消失"次数。
- `cmd /c tools\build-test.bat`：删除功能的离线回归（只在临时目录建假库）。

### 15:35 闪屏的真正根因 + 三个新功能

**「壁纸时不时变回系统默认、几秒后恢复动态」—— 播放器在崩溃，不是显示层问题。**

日志非常明确：每次都是先出现

```
[V] 播到末尾，回到开头（已播 20 帧）
<这里什么都没有，进程直接消失>
```

播完一轮调 `IMFSourceReader::SetCurrentPosition` 回到 0，**进程静默崩溃**（没有任何托管异常，
所以连 `!!!` 日志都没有）。窗口随进程销毁 → 桌面露出系统壁纸 → 看门狗 4 秒后拉回来。
这就是那个"闪屏"。视频约 30 秒一轮，所以是"时不时"。

修法：
- **不再调 `SetCurrentPosition`**，改成 `reader.Dispose()` 后重新 `MfVideo.Reader.Open(path)`。
  多花约 0.2 秒，但绝不崩；这段时间窗口保留最后一帧，画面不断层。
  重开后若尺寸变了会重建帧缓冲。
- 兜底再补一层：`Dynamic.Start` 里挂 `proc.Exited`，**进程一退出就立刻恢复**（不再等看门狗那 4 秒）。
  主动停止用 `lastStopTick` 区分，避免自己关掉又被拉起来。
- 暂停期间每 500ms 重画一次最后一帧：桌面被别的程序触发重绘时窗口表面可能被擦掉，
  不补画就会闪；这是"暂停时也可能闪一下"的防线。
- 全屏检测跳过自家窗口：欢迎动画、大图预览都是全屏，以前会把它们当成"用户在看全屏视频"，
  于是暂停一次、动画结束再恢复，开机白闪一下。

实测 `loop_test.py 70`：70 秒内循环 2 次，**播放器 PID 全程没变，恢复次数 0**。

**壁纸库删除功能**
- 卡片右上角圆形 ×（悬停才出现，避免误点）+ 右键菜单「删除这张（连文件一起删）」，都要二次确认。
- `LibraryStore.Delete`：先重写 `index.json`（留 `.bak`），再删主文件/缩略图/封面。
  顺序不能反 —— 先删文件、索引写失败的话，库里会留一条指向不存在文件的黑卡。
- 删文件前校验路径必须在库根之下，`index.json` 里被人写了 `../` 也删不动库外的东西。
- 删的是当前壁纸就自动换下一张；删的是正在播的视频先停播放器（否则文件被占用删不掉）。
- 离线回归：`tools/build-test.bat` → 8 项检查（含越界保护）全过。

**顶栏分类改成「本地资源 / 在线资源」**
- 原来三个 chip（壁纸站 / B站图集 / B站视频）把技术细节摊给了用户，现在只留两个。
- 在线具体搜哪儿由程序判断：**中文 → B 站图集，英文 → 壁纸站**，用户不用再选。
- 「静态图 / 动态视频」的切换挪到**在线结果条右侧**（动态视频 = B 站 1080P），
  既保留了动态壁纸的来源，又不占搜索栏。
- `RefreshList` 里同步 `UpdateChips`，否则结果条上的两个小标签显隐不跟着变。

---

### 历史记录（08:xx ~ 11:xx 的失败过程，留作不再重踩）

### 10:18 那轮探测的结果（4 组全灭，但只有 2 组是有效实验）

日志（`%TEMP%\wallpaper-debug.log`）里四组都 `洋红 0.0%`，但要看「挂载前」那一行：

| 组 | 做法 | 挂载前洋红 | 判定 |
|---|---|---|---|
| A | Progman 内 WorkerW + 普通 GDI 子窗口 | 97.6% | **有效实验**，挂载后 0% → 真不可见 |
| B | WorkerW + `UpdateLayeredWindow` | 2.4% | **无效实验**：挂载前就没画出东西 |
| C | Progman + `UpdateLayeredWindow` + 最底层 | 12.6% | **无效实验**：同上，而且最底层会被 WorkerW 的壁纸盖住 |
| D | Progman + 普通 GDI 子窗口 | 95.8% | **有效实验**，挂载后 0% → 真不可见 |

结论：**GDI 子窗口挂在 Progman / WorkerW 下都不参与合成**（Progman 带
`WS_EX_NOREDIRECTIONBITMAP`，它压根没有 GDI 表面）。你看到的「闪几次红屏」就是每组
建窗口后、挂载前那一瞬的顶层全屏洋红弹窗。

### 微软对 24H2 桌面改版的官方口径（这才是正确做法）

> 桌面从 ListView 窗口分离后，不再创建多个顶级 WorkerW；`Progman` 作为顶级窗口改用
> `WS_EX_NOREDIRECTIONBITMAP` 创建，因此它没有 GDI 内容；`SHELLDLL_DefView` 变成带
> `WS_EX_LAYERED` 的子窗口；桌面「升起」时会在 DefView 下方创建一个 WorkerW 子窗口渲染壁纸。
> 应用若要贴壁纸，需要**自己创建一个 WS_EX_LAYERED 子窗口**，Z 序**在 DefView 之下、WorkerW 之上**，
> 并且用 **`SetLayeredWindowAttributes(bAlpha = 0xFF)`** 打开，这样才能往它做 DX Blt 呈现而不掉性能。

对照本机窗口树，这四件事全都对得上：`Progman 扩展样式=0x00200080`；
`子窗口[0] SHELLDLL_DefView 扩展=0x00080000`（就是 LAYERED）；`子窗口[1] WorkerW 1920x1080`。

所以之前几轮失败的共同原因是：**用 `UpdateLayeredWindow` 而不是 `SetLayeredWindowAttributes`**，
加上**漏了 `WS_CHILD`**。
（注意：跨进程父子关系**只能**用 `SetParent` 建立，见下一节 10:30 的实测，不是「SetParent 危险」。）

### 10:30 那轮：两组都是无效实验，错在「建窗时就指定跨进程父窗口」

日志原文：
```
[1/2] M1 · Progman 下的 WS_CHILD + WS_EX_LAYERED 子窗口
      建窗失败 err=183，跳过这组
[2/2] M2 · 同上，但先建普通子窗口
      窗口=0x1401e8 父=0x10104（Progman，符合要求）
      扩展样式 0x00000000 -> 0x00000000
      SetLayeredWindowAttributes(0, 255, LWA_ALPHA) 失败 err=87
```

两个错误码把原因钉死了：

- `err=183` = `ERROR_ALREADY_EXISTS`：`CreateWindowEx(WS_CHILD, parent=Progman)` **跨进程指定父窗口建不出来**。
- `err=87` = `ERROR_INVALID_PARAMETER`：`SetLayeredWindowAttributes` 只在窗口**带 `WS_EX_LAYERED`** 时有效。
  而上面那行 `扩展样式 0x00000000 -> 0x00000000` 说明 `SetWindowLong` 写了但**读回没变** ——
  那个窗口的样式被系统静默拒改，它不是我们能掌控的窗口，`SetLayeredWindowAttributes` 自然无效。

**所以这两组都没测到「合成」，只是测试代码本身错了。** 结论修正为：

> 跨进程父子关系**只能在建窗之后用 `SetParent` 建立**；创建时把别人的窗口当父窗口是不可用路径。

同时注意到一条反证：`SHELLDLL_DefView` 自己就是 **Progman 下的 `WS_EX_LAYERED` 子窗口**，
图标显示得好好的 —— 说明「分层子窗口能被合成」在本机成立，这条路本来就该通。

### 现在的探测（`app/WallpaperPlayer.cs`）

每一步都打印返回值与 `GetLastError`，任何一格对不上都会在日志里明写，不再出现「跳过这组但不知道为什么」。

1. `[3] 自检`：顶层洋红弹窗采一次样，确认「`StretchDIBits` 绘制 + 屏幕采样」可用（会闪 1 次红屏）。
2. `M1`：**本进程建弹窗（带 `WS_EX_LAYERED`，先不 `WS_VISIBLE`，所以挂载前不闪红屏）**
   → `SetParent(Progman)` → 补 `WS_CHILD|VISIBLE|CLIPSIBLINGS|CLIPCHILDREN`
   → `SetLayeredWindowAttributes(0, 255, LWA_ALPHA)` → `SetWindowPos(hwnd, DefView, …)`
   （插到图标层**正下方**、WorkerW 之上）→ 画洋红 → 采样两次。
3. `M2`：同上，但先在挂载前建**不带** LAYERED 的弹窗，挂载之后才补 `WS_EX_LAYERED`
   （绕开「挂载瞬间带 LAYERED」的合成限制）。两种顺序都试，一次编译就能定案。
4. **置顶复查**：原位看不见时，把 Z 序改成最上层再采一次（约 700ms，会短暂盖住图标）。
   这一步把两种失败彻底分开：
   - 置顶也看不见 → **根本不被合成**，只能上 `D3D11 + DirectComposition` 交换链；
   - 置顶能看见 → 合成没问题，只是 Z 序 / 被壁纸层盖住，调位置即可。
5. 每组用完立刻 `DestroyWindow`；收尾再让 Explorer 重画一次桌面（`WM_COMMAND 0x7402` + `RedrawWindow(DefView)`），图标不会消失。
6. 看门狗 26 秒（早于管理器 32 秒超时），触发时写明「卡在哪一个阶段」。

### 视频播放（`app/PlayerVideo.cs` + `app/MfVideo.cs`，`--video`）

跟探测共用同一套绘制（`PlayerProgram.PaintWindow`），区别只是 DIB 里放的是解码帧：

- 解码：Media Foundation `IMFSourceReader`，直接谈 `MFVideoFormat_RGB32` 输出，
  拷进 DIB（处理 stride / 自下而上两种情况），`RtlMoveMemory` 按行搬，零 GDI 之外的依赖。
  **不用 WPF `MediaElement`**：微软那份说明里写明 MPV/VLC 这类自带渲染器的后端能工作、**WMF 后端不行**，
  而且 `MediaElement` 也拿不到像素。
- 呈现：`StretchDIBits` 按「填充」规则居中裁切，1080p 一帧 8MB，30fps 大概占半个核的 15%。
- 循环：播到末尾 `SetCurrentPosition(0)` 回到开头，失败就停（日志会写）。
- 节奏：按 MF 的时间戳（100ns）排期，画完再补睡，周期 = max(绘制耗时, 帧间隔)，不会越播越慢。
- 控制：`WM_APP+1/2/3`（暂停/继续/退出）。窗口现在是 **Progman 的子窗口**，
  `FindWindow` 只能找顶层窗口，所以 `Dynamic.ControlHandle()` 已改成再用 `FindWindowEx(Progman, ...)` 找一遍。
- 建窗顺序与探测完全一致（`CreateLayerChild`）：本进程建弹窗 → `SetParent` → 补 `WS_CHILD` → 设 alpha，
  任意一步失败都会返回具体原因，不会静默播不出来。
- 播放前会先铺一次洋红自检，日志写「自检：屏幕洋红 x%」——这一行就说明这个窗口到底显不显示。

`PlayerVideo.cs` / `MfVideo.cs` 是可选件：`PlayerProgram` 用反射调它，`build.bat` 里若编译失败
会自动退回只编探测模式（错误留在 `build-player-video.log`），不会把播放器整体拖死。

## 五、还没做的

1. 动态壁纸：等这次编译后跑一遍。`VISIBLE` → 直接点视频卡片的「播放为动态壁纸」，那里已经接好 MF 解码；
   `NOT_VISIBLE` → 只剩 `D3D11 + DirectComposition` 交换链一条路（`CreateSwapChainForComposition` →
   `DCompositionCreateDevice` → `CreateTargetForHwnd` → `SetContent` → `Commit`），要手写十几个 COM 接口的 vtable。
2. 多显示器：本机是单屏（Dell G3 / 1920×1080），按单屏实现，副屏不做。
3. B 站 4K：需要登录，匿名上限 1080P，当前够用。

已补完的：全屏游戏/放映自动暂停（`SHQueryUserNotificationState`，状态 2/3/4 时暂停，退出恢复，每 3 秒轮询一次）。

---

## 六、竖屏壁纸自适应 + 静态图动效（11-11 下午）

### 1. 竖屏壁纸看不到全图 —— `WallpaperFit.cs`（新文件）

以前所有图一律 `WallpaperStyle=10`（填充）。填充是按屏幕比例裁切的：
横图基本无感，但 9:16 的竖图会被放大三四倍、只剩中间一条，就是用户说的
「放大后比例很奇怪，看不到全图」。

现在分两种情况：

| 情况 | 做法 |
| --- | --- |
| 图和屏幕宽高比差 ≤2% | 原图直接填充，不重编码、不掉画质 |
| 差得多（竖图 / 超宽 / 方图） | 合成一张「正好屏幕尺寸」的图：原图完整居中（contain）+ 同图缩到 1/28 再放大当模糊底衬（压暗 69%），再按填充设 |

顺带认 EXIF Orientation（0x0112）：手机竖拍图的像素其实是横的、靠一条旋转标记转过来，
不认它比例会彻底乱掉。

缓存：`%LOCALAPPDATA%\WallpaperManager\fit\<FNV指纹>.jpg`，指纹含「路径+大小+mtime+屏幕尺寸+算法版本」，
最多 80 张（超了删最旧）。改了合成规则要把 `AlgoVersion` +1，否则大家会一直命中旧缓存。

视频同步改了 `PlayerProgram.PaintTo`：比例一致就一次拉伸铺满；不一致就底衬 + 整帧居中。
每帧多一次 1/10 小图缩放 + 一次全屏 blt，1080p30 实测无压力。

### 2. 静态图也能「动态播放」—— `PlayerImage.cs`（新文件）

新增 `WallpaperPlayer.exe --image <path>`：图先过 WallpaperFit 拿到屏幕尺寸的合成图，
按「铺满」画进 1.07 倍屏幕的帧缓冲，之后每帧只做一件事：按时间算出一块源矩形，`StretchDIBits` 铺满屏。

- 缩放 `1.00 ~ 1.07`（最小正好完整显示），26 秒一个来回；
- 推近和漂移用三个不同相位的正弦，看起来像慢慢运镜而不是机械来回；
- 走的是和视频完全相同的 mount 8 通道（Progman 分层子窗口 + GDI 直画），不看图标、不闪。

条目新增 `motion` 字段（写进 `index.json`）。入口两处：
预览面板的第二个按钮「以动态方式播放（缓慢缩放）」、卡片右键同一项；
关掉就退回系统静态壁纸。`images/` 里的散图第一次设置时会补一条最小索引记录，否则开关下次启动就丢。

看门狗和「进程退出后恢复」改用 `MotionCapable()` + `StartMotion()`，视频与动效图共用一条恢复路径。

### 3. 顺带修的

- `FilterPortrait` 默认 `true` → `false`。以前「B 站图集里竖版一律丢」，理由是「填充会裁两边」——
  现在这个理由不成立了，默认改成收进来。现有配置里已经写了 `true` 的按用户选择保留，这次手动改成了 false。
- 卡片缩略图：竖图 `UniformToFill` → `Uniform`（完整显示，两侧留暗底），横图仍 `UniformToFill`。
  否则列表里 9:16 的图被裁成中间一条，同样「看不出这张壁纸长什么样」。

### 4. 验证

- `tools/build-test-fit.bat` + `tools/test_fit.cs`：造「上红下蓝」的竖图 → Prepare → 断言输出尺寸等于屏幕、
  画面里能同时看到红和蓝（= 整张图都进了画面）、两侧不是死黑、同图第二次命中缓存、同比例图不重编码、超宽图也走自适应。12 项全过。
- `tools/build-fitprobe.bat` + `tools/fitprobe.cs`：DPI 感知进程里真设一次系统壁纸 → BitBlt 抓屏 →
  断言红带在 y=16、蓝带在 y=1016（画面高 1080），跑完恢复原壁纸。
- `tools/motion_check.py`：连抓两帧比像素，动效运行时全屏 15% 像素在变。
- `tools/grab_bmp.py`：全屏 BMP 截图（不依赖 PIL），用来肉眼确认桌面层到底显示了什么。

## 七、托盘「停止并恢复静态壁纸」失效（11-11 16:20–16:40）

### 症状
右下角托盘点「动态壁纸 → 停止并恢复静态壁纸」没有任何变化，壁纸状态改不回来。

### 根因（日志实锤）
```
16:22:49.915  收到停止指令          ← Dynamic.Stop() 真的执行了
16:22:49.972  [P] 图片动效结束
16:22:50.033  动态壁纸启动：…（图片动效）   ← 60ms 后又起了一个播放器
```
`StopDynamic(true)` 先 `Dynamic.Stop()`，紧接着 `ApplyItem(cur)`；而 `ApplyItem` 一看到
`it.Motion == true` 就 `StartImageMotion()` —— 停和开在同一个函数里对冲，等于点了没反应。
静态图动效是上一版新加的，所以这条路径之前没人踩过。

### 改法
- `ApplyItem(it, bool allowMotion)` 重载：`false` = 强制静态（视频退到封面图），
  内部直接 `Dynamic.Stop()` + 清 `DynamicEnabled`，不再走「看到动效标记就开播」的分支。
- `StopDynamic(true)` 走 `ApplyItem(cur, false)`；**只停当前，不动这张图的 `motion` 标记**
  （标记是用户的显式选择，要永久改回静态走卡片右键「改回静态壁纸」）。
- `MotionCapable` 不再要求 `ImagePath` 已存在：本地图集的 `imagePath` 本来就是空的
  （文件就在库里），以前这条判断直接把「开机恢复动效」判死。
  新增 `ResolveMotionImage()`：已下载封面 → 库内原图 → 缩略图 → `EnsureCover` 联网补。
- 托盘菜单：首行显示「当前：动态播放 / 静态壁纸 xxx」，打开菜单时按 `Config` 刷新文案与勾选；
  新增「重新应用当前壁纸」（桌面被别的程序冲掉时一键贴回）；每项点击都写日志 `托盘：…`。
- `tray.Text` 跟随状态（动态播放中 / 静态壁纸：标题）。
- `DoSet` 成功/失败都写日志（以前只在界面上提示，出问题没法从日志判断）。
- `build.bat`：先杀管理器再杀播放器（反过来会让看门狗在 60ms 内复活播放器、锁住 exe），
  播放器 exe 的替换也加了重试 + 失败中止 —— 16:27 那次构建就静默留下了**旧播放器**，
  管理器是新的、播放器是老版本，看起来像「改了没用」。

### 验证
`--stopdynamic` 自检开关（启动 10 秒后自动执行一次 `StopDynamic(true)`，写日志后退出）：
```
16:34:45  动态壁纸启动：…（图片动效）
16:34:55  自检 stop 前 动态=True 播放器=True cursor=16
16:34:55  收到停止指令 / [P] 图片动效结束
16:34:55  停止动态壁纸（恢复静态=True）
16:34:56  设为静态壁纸：… fit=10
16:34:56  自检 stop 后 动态=False 播放器=False
```
stop 之后再无「动态壁纸启动」，配置 `dynamicEnabled=false`，抓屏确认桌面是静态图（无黑屏/绿边）。

## 八、静态图「场景动效」（11-11 17:00–17:30）

用户要的不是缩放，是「云会飘、浪会涌」这种按生活常识的运动。分两条路：
A 本地算（免费、即时）、B 云端图生视频（要积分、要等）。**这轮做的是 A**，B 留作卡片上的单独入口（未做）。

### 做法：按区域判断，不给整张图贴一个类别

`app/PlayerEffect.cs`
1. **找地平线**：320×180 缩图上算每行水平边缘能量，在 32%~86% 高度里取最大，且要大于中位数的 1.35 倍，
   否则认为没有地平线。
2. **天空**（地平线以上，亮度 > 0.28）：云整片缓慢漂移 + 低频形变。
3. **水面**（地平线以下偏蓝/青、且纵向平缓）：长涌 + 碎浪两套横移 + 纵向涌起 + 浪花高光呼吸。
4. **雨丝**（水平梯度 ≫ 垂直梯度、整体偏暗）：叠两组不同速度/长度的斜线。
5. **夜景**（暗 + 小亮点 > 1.2%）：16×16 分块按空间相位轻微闪烁，暗块不参与。
6. **人物**（暖色大块集中在中部、且不是水面）：**整张刚性漂移**，不做分区位移。

### 两个关键设计（都是踩坑改出来的，别再改回混合）

- **视差衰减**：位移量在地平线处为 0，往上/往下逐渐放开（`ramp = smoothstep`）。
  第一版是「整块天空平移 + 交界按权重混合」，结果交界线上的亮物（太阳圆盘、城市天际线）
  被混合成**重影 + 一刀切的横线** —— 因为混的是两个错位的副本。现在交界处位移为 0，天然连续。
- **云漂移必须有界**：原型写的是 `2.2px * t` 一直往右推，8 秒的演示片看不出问题，
  但真实壁纸上跑一分钟后整片天空会被边缘复制填满（画面被拉成条）。现在是两组慢正弦叠加，
  位移有界在 ±22px（1080p）。
- **人物画必须刚性**：分区位移会让「头部在天空区被推 20px、肩膀不动」，人被拧一下。
  `Subject && !Water` 直接走整张漂移。

### 动效方式怎么存
`WallpaperItem.Effect`（`fx`）: **0 = 场景动效（默认）**，1 = 缓慢缩放。数值在管理器、`index.json`、
播放器 `--effect` 参数三处必须完全一致 —— 中途把它们写反过一次：UI 选了场景，播放器却按 0 走了缩放，
日志里出现「图片动效 场景」之后紧接着「开始缓慢缩放」，排查了半天。

### 入口
- 卡片右键 →「动态播放」子菜单：场景动效 ✓ / 缓慢缩放 ✓ / 关闭
- 预览面板：`以场景动效播放` + `以缓慢缩放播放`（已在播时变成 `改回静态壁纸` + `换成…`）
- `App.SetMotionFx(it, fx)` 一处收口：写 `index.json` → `ApplyItem` → 播放器

### 性能
1080p 单帧 **2~8ms**（30fps 预算 33ms）。行位移用 `Buffer.MemoryCopy` 整段搬 + 边缘复制补头尾，
不是逐像素循环。播放循环里每 150 帧统计一次耗时，超过预算八成自动放宽间隔（最低 15fps），
4K 屏不会把一个核吃满。

### 验证
- `WallpaperPlayer.exe --fxdump <图> <目录>`：不碰桌面，直接渲染 0/2.5/5/7.5 秒的 PNG + 报每帧耗时。
  `tools/run-fxdump-all.bat` 把库里 13 张全跑一遍，确认每张都有分类结果、都不超过 7ms。
- `WallpaperPlayer.exe --fxvideo <图> <目录> [秒] [fps]`：逐帧 jpg，用 imageio 合成 mp4。
  产物在 `demo/scene-*.mp4`（海景 / 赛博朋克 / 竖版人物）。
- `tools/fx_e2e.py`：真起播放器挂 mount 8，然后**从播放器窗口的 DC 抓两帧**比对。
  只抓全屏没用 —— 会被前台窗口挡住，测出来永远是 1%（踩过）。
  实测窗口内容 3 秒变化 16.8%，日志确认 `[P] 场景分析：地平线=47% 区域=云层 灯光`、7.6ms/帧。
- `--fxselftest` 自检开关：启动 3 秒后 `SetMotionFx(it,0)`，8 秒后换成 `1`，16 秒后 `StopDynamic(true)`。
  日志里出现两条「动态壁纸启动」分别是 `（图片动效 场景）` 和 `（图片动效 缩放）`，
  期间 `播放器=True 窗口=True`，停止后 `动态=False 播放器=False`。

## 九、托盘「换一张」先闪别的图（11-11 18:10）

### 现象
托盘点「换一张」，桌面先露出系统静态壁纸（通常正是刚下载的那张），过一会儿才切到目标壁纸。

### 根因（两条叠加）
1. `Dynamic.Launch` 旧顺序是 `Stop()` → `Start()`。旧播放器窗口先销毁，新进程还要 250~400ms 才画首帧，
   这半秒桌面没有任何动态层，直接露出系统壁纸。
2. 看门狗 4 秒一轮，正好撞上「进程在、窗口还没建」的空档，判 `窗口=False` 又补起一个播放器。
   日志实例：启动后 **16ms** 就报掉线，桌面空档被拉到近 1 秒。

### 修法
| 文件 | 改动 |
| --- | --- |
| `Dynamic.cs` | `Launch` 改交接（handoff）：先起新进程（附 `--keep-old`）→ `WaitPlayerWindow(pid)` 等**属于新 pid** 的 `WpPlayerWnd` → 再 `StopOld()` 收旧的 |
| `Dynamic.cs` | 等待窗口必须按 pid 校验（`EnumWindows` + `EnumChildWindows(Progman)`），只按类名会把旧窗口认成新窗口 |
| `WallpaperPlayer.cs` | `--keep-old` → `KeepOld`，`CloseLeftovers()` 直接返回；否则新进程会把还在撑画面的旧窗口关掉 |
| `Program.cs` | `WatchdogTick`：启动后 4 秒宽限（`Dynamic.LastLaunchTick`）+ 连续 2 轮缺失才重启（`watchMisses`） |
| `Program.cs` | `ApplyItem(!allowMotion)` 不再立刻 `Dynamic.Stop()`；`DoSet` 里 `Desk.SetWallpaper` 成功后才停 |
| `Program.cs` | `StopDynamic(true)` 不再开头 `Dynamic.Stop()`，交给 `ApplyItem(cur,false)` → `DoSet` 收尾 |

### 实测
`--rotatetest`（3 秒后连点两次换一张）与真实托盘点击：

```
18:13:11.126  托盘：换一张
18:13:11.139  动态壁纸启动：edgewalker.mp4（交接模式）
18:13:11.386  新播放器首帧提交
18:13:11.388  旧播放器才收到停止指令     ← 24ms 后，旧顺序是反的
18:13:11.446  旧播放器结束
```

切换耗时约 495ms（含等首帧），全程 `WallpaperPlayer` 进程数 = 1，无看门狗误判。
新窗口 Z 序在旧窗口之上（`[1]新 [2]旧`），所以交接期间看到的是新画面。

### 附带工具
- `tools/handofftest.cs` + `tools/build-handofftest.bat`：命令行用两个 mp4 模拟换台，打印切换耗时/进程数。
- 注意：测试 exe 必须输出到 `app\bin\`，否则 `Dynamic.PlayerPath` 找不到播放器。

## 十、人物图局部运动（11-12 全天）— **已按主人要求整体删除，留档备查**

> **11-12 11:00 更新**：主人看过实机效果后判定「整块人物在扭曲，不是我想要的那种局部晃动」，
> 决定**不要纯算法这条路**，改成云端生成 3 秒视频（见第十一节）。`app/PlayerPerson.cs` 已删除，
> `fx=2` 的含义从「人物动效」改成「云端动效」。本节保留，是因为里面关于
> 「为什么像素层面分不出头发和帽檐」的实测结论以后还会被问到 —— 别再重走一遍。
>
> 一句话结论：**单张静态图靠像素判据做「局部飘动」是死路**。
> 头发和帽檐在梯度图上都是「边」，开运算会把致密长发整块抹掉（覆盖率 0.0%），
> 沿轮廓取薄带则必然把帽檐一起带上（帽檐那块 40px 厚，跟着摆 60% 像素）。
> 三条路（渐变气泡 / 轮廓薄带 / 细结构筛选）都试过，全部失败。

用户的原话是「衣服头发也能飘动」，交接文档里记成 P1-3。这轮把它做成了 `fx=2`。
**硬约束有两条**：① 不引入 ONNX / OpenCV 之类模型，保持「零下载、免安装」；
② 不许只对库里那一张调参 —— 用户明确说过「以后我还会下载新的人物图片」。

### 为什么不能靠「找人物」的通用套路

量产二次元人物图里，前景和背景的区分信号都被逐个否定掉了（都有实测）：

| 试过的信号 | 结果 |
| --- | --- |
| 颜色（肤色 / 暖色） | 粉调插画整张 94% 命中，背景也是暖色 → 废 |
| 亮度 | 背景是高亮虚化，主体反而不是最亮的 79% 区域 → 废 |
| 「最大细节块」 | 城市封面 14 张里 13 张误判；真实照片上抓到了**美国国旗** |
| 皮肤色质心 | 在满分辨率下落到了**帽子**上（57%,38%），不是脸 |

最后能用的只有一个：**梯度能量**。主体（头发丝、格纹、布料褶皱）边缘密集，虚化背景平滑。
实测主体区 5.7~8.5、背景区 1.3~4.4，分得开。

### 算法（`app/PlayerPerson.cs`，约 600 行，纯 GDI+ + unsafe 指针）

320×180 缩图上：

1. 逐像素局部梯度能量 `e`，取中位数 `med`，阈值 `thr = max(3.2, med*2.4)` —— 自适应，不写死绝对值。
2. 膨胀 4 次 → **中心加权选块**：`score = area * (1-0.85*|cx-0.5|) * (1-0.55*max(0,cy-0.78))`
   （加权是为了不再选到边角的旗子/装饰物）。
3. 填洞（**只填小于总面积 2% 的洞**，见踩坑）→ 腐蚀 6 次 → 再选一次块。
4. **找脸**：在主体上半 65% 里挑「最平滑的大块」（`e < med*0.9`），再用四条硬指标验：
   宽度 ≤ 0.24、高度 ≤ 0.40、长宽比 0.55~1.90、内部边缘 ≤ 3.5。全部通过才算锚点。
5. 权重场 = 以脸为中心的「摇摆气泡」：`d<1` 处为 0（脸完全锁死，绝不形变），
   往外 1→2.6 上升，再衰减到 0。有遮罩（覆盖率 ≥15%）时放远到 6.0，否则收到 3.6。
6. 渲染：**逐像素逆向采样** + 权重场双线性插值；`sin(a+b)` 拆开预计算三角函数表；
   `ampX = w*0.009f`、`ampY = h*0.0035f`；按包围盒整行 `Buffer.MemoryCopy`。

### 拒绝机制（这一条最重要，别删）

只要脸的四条指标有一条不过，就 **`Info.Ok = false` + 写清楚原因**，`PlayerImage.PlayPerson`
直接退化去 `PlayScene`。宁可这张走场景动效，也不要在人家脸上糊一块会扭的色块。

日志实例（拒绝时）：`找到的「脸」比例不对（长宽比 0.47）` / `太粗糙` / `太大`。
真实照片和复杂背景（那张奥巴马）目前就是走拒绝路径 —— 这是设计，不是 bug。

### 踩坑记录

- **`fill_holes` 把被手臂围住的背景也填了** → 画面 62% 都在动。改成只填空洞 < 2% 面积。
- **膨胀带出四角光晕**：做了参数扫描，以「四角像素变化占比」当客观指标（人物不该占满四角），
  定到 `thr=med*2.4, 膨胀4, 腐蚀6`，四角变化 0.0%。
- **脸锚点落在帽子上**（全分辨率下头发被拖着走，脸上留一块死板区）→ 换「最平滑块」。
  这个信号对阈值 k=0.6~1.2 都不敏感，落在 46%,61%，肉眼核对确实在脸上。
- **`Dynamic.StartImage` 把 effect 钳在 0..1**，导致 fx=2 被静默改成场景动效。
  `Dynamic.cs` 和 `LibraryStore.cs`（`SetMotion` 与 index 载入两处）都改成 `0..2`。
  这个坑和第八节「方式怎么存」是同一个坑的第二次 —— 加新 fx 值时，这三个地方必须一起改。
- **C# `fixed` 指针逃出作用域**（CS0213 系列）→ 把 `fixed (byte* wp = wt)` 提到整行循环外面。

### 实测数据（1080p）

| 项目 | 数字 |
| --- | --- |
| 单帧耗时 | 12.6 ~ 15.0ms（30fps 预算 33ms） |
| 脸部区域变化 | 5%（锁住了，只剩极轻微呼吸） |
| 长发区域变化 | 60% |
| 衣袖区域变化 | 52% |
| 四角变化 | 0.0% |
| 主体遮罩覆盖率 | 37.6%（当前库内样图） |

### 入口

- `WallpaperItem.Effect = 2`（`fx: 2`），管理器 / `index.json` / 播放器 `--effect` 三处一致。
- 卡片右键 →「人物动效：头发 / 衣摆微动」（`fx2`，子菜单现在 4 项）。
- 预览面板第 3 个按钮自适应：`App.LooksLikePerson(it)` 命中就写「以人物动效播放」，
  否则「以缓慢缩放播放」；已在播时按 场景→人物→缩放 轮换。
- `App.LooksLikePerson` 会把结果缓存在 `WallpaperItem.PersonHint`（运行时字段，不落盘），
  判定区间覆盖率 0.03~0.75，日志打「人物动效推荐检查」。

### 验证工具

- `tools/build-player-only.bat`：只编译替换 `WallpaperPlayer.exe`、不起任何进程 —— 调算法时用它，
  比 `build.bat`（末尾会 `start` 管理器）干净。
- `WallpaperPlayer.exe --fxdump <图> <目录> [effect] [w h]`：现在支持 effect 2，
  并且可以**强制指定渲染尺寸**（4K 计时不需要 4K 屏）。会额外吐出 `weights-*.png` 便于看遮罩。
- `WallpaperPlayer.exe --fxvideo <图> <目录> [秒] [fps] [effect]`：产物 `demo/person-sway.gif`。
- `--persontest` 自检：3 秒 `SetMotionFx(it,2)` → 10 秒报状态 → 16 秒 `StopDynamic(true)`。
  实测日志：
  ```
  人物动效推荐检查：biliart-17403571-4 → 像人物（主体遮罩=37.6%  脸部锚点=46%,61%  梯度中位数=2.83  阈值=6.80）
  动态壁纸启动：… fps=30（图片动效 人物） mount=8（交接模式）
  [P] 人物分析：主体遮罩=37.6%  脸部锚点=46%,61% …
  [P] 人物动效帧耗时平均 15.0ms（30fps 预算 33ms）
  自检 --persontest(10s)：动态=True 播放器=True 窗口=True effect=2 名字=人物动效：头发/衣摆微动
  ```
- `tools/person_live.py <图> [effect] [秒]`：一条命令跑完「起播放器 → 最小化挡桌面的窗口 →
  隔 2.4 秒抓两帧 → 分区统计 → 收进程」，用来验证分区变化百分比。
- 重启恢复也验过：`config.json` 里 `dynamicEnabled=true` + `lastDynamicId` 指向该条目，
  启动管理器后播放器命令行是 `--image … --effect 2`，日志 `图片动效模式：… effect=2`、
  首帧上屏、12.6ms/帧，`WallpaperPlayer` 进程数 1。

### 还没做的

- 真实照片 / 复杂背景一律走拒绝 → 场景动效。要提高命中率，得再拿十几张「用户会真的下载的图」当样本
  重新调「找脸」这一步（`Info.Note` 已经把拒绝原因写出来了，收集样本很方便）。
- 头发只做整体摇摆，没做「发丝级」细分；袖子同理。做细分需要按区域再分层权重，工作量另算。

## 十一、云端动效 + 日常便利（11-12 11:00–11:20）

### 1. 拆掉纯算法，换成「下单 → 到货 → 自动上桌」

`fx=2` 现在是**云端动效**，落地方式见 `app/CloudGen.cs`：

| 环节 | 谁做 | 怎么做 |
| --- | --- | --- |
| 下单 | 应用内 | 右键「云端生成动效视频（3 秒 · 无水印）」或预览面板第三个按钮 → 往 `%APPDATA%\wallpaper\jobs\queue\<id>.json` 写工单 |
| 生产 | 应用外 | 我这边的图生视频能力，或主人自己的脚本；`config.json` 里填 `genCmd` 就能让应用下单时顺手起那个程序 |
| 收货 | 应用内 | 管理器每 3 秒扫队列，`videos\<id>.mp4` 一出现就入库、标云端动效；如果这张是刚下单的（`pendingCloudId`），**立刻换上**，不用再点一次 |
| 超时 | 应用内 | 30 分钟没到货标记失败，卡片提示「上次生成失败了」 |

为什么应用内不直连云端：要账号、要额度、要密钥，装到别人机器上就废，还违背免安装。
应用只当「订单 + 收货台」，一个字节的网络请求都不发。

**关键取舍**：`effect=2` 但视频还没到货时，播放器**不会**硬播一堆坏帧 ——
`StartMotion` / `StartImageMotion` 都会临时降到场景动效顶着，视频一到就自动换回来。
`PlayerImage.ModeCloud` 保留编号只为对齐 `index.json`，真走到那里会打日志并兜底场景动效。

**实测日志**（`--cloudtest`，模拟「下单 6 秒后片子到货」）：

```
10:59:12.594  云端生成：已下单 biliart-17403571-4（3 秒）→ …\jobs\queue\biliart-17403571-4.json
10:59:21.622  云端生成：到货 biliart-17403571-4 → D:\Wallpaper\videos\biliart-17403571-4.mp4（3160KB）
10:59:21.625  云端生成：到货处理 id=biliart-17403571-4  找到条目=True  要自动换=True
10:59:21.632  云端生成：换上新生成的飘动视频（高清动漫壁纸美图搬运·72（第 4 张））
10:59:21.652  动态壁纸启动：D:\Wallpaper\videos\biliart-17403571-4.mp4 fps=30 mount=8
```

### 2. 出片规矩（主人当场定的两条）

1. **时长 ≤ 3 秒**（省额度）：`CloudGen.MaxSeconds = 3`，工单里 `seconds` 写死 3，
   `config.json` 的 `genSeconds` 也夹在 1~3。
2. **必须去水印**：本服务出的片子左上角带「AI生成」，实测相对位置
   `x0.7% y0.8% 宽6.6% 高6%`。

后处理脚本 `tools/video_fix.py`（一条命令搞定三件事）：

```
python tools/video_fix.py <输入.mp4> <输出.mp4> --seconds 3
```

- `delogo` 用周边像素插值补掉水印（模糊背景上完全看不出补丁，已用三帧放大对比确认）；
- 裁到 3 秒；
- **正放 + 倒放（ping-pong）做无缝循环**：单条片子只有 L 秒时，「首尾同帧」在文件层面
  数学上不可能；正放倒放是唯一 0 跳帧的办法，而头发/衣摆这种来回摆动倒放看起来就是自然摆动。
- 用到的 ffmpeg 来自 `pip install imageio-ffmpeg`（静态二进制，不进应用、不进发行包）。

现存那条片子已经过这道处理：`1344x768 5.17s → 6.00s（3s 正放 + 3s 倒放）`，原片留在 `videos\_raw\`。

### 3. 日常便利四件

| 功能 | 入口 | 落点 |
| --- | --- | --- |
| 收藏 | 卡片左上角 ☆ / 右键「收藏这张」/ 顶栏「☆ 收藏 N」只看收藏 | `index.json` 的 `fav` 字段（`LibraryStore.SetFav`） |
| 历史回滚 | **F5** 一键回上一张，连按一路往回退（最多 24 步） | `Program.history`，`ApplyItem` 时入栈 |
| 全局快捷键 | **Ctrl+Alt+Space** 暂停/继续，**Ctrl+Alt+N** 换下一张，窗口不在前台也生效 | `RegisterHotKey` + `WM_HOTKEY`（`MainWindow.WndProc`）。没用 Ctrl+Alt+方向键——那是部分显卡的「旋转屏幕」 |
| 拖拽导入 | 图片/文件夹/视频直接拖进窗口 | `LibraryStore.ImportFile`：图片进 `images\`（靠目录扫描自动收录），视频进 `videos\` 并补索引；同名自动 `-2/-3`，不动原文件；文件夹最多走两层、300 个文件封顶 |

自检开关 `--dailytest [文件夹]` 的实测输出：

```
11:09:12.373  全局快捷键：暂停/继续=True  换下一张=True（Ctrl+Alt+Space / Ctrl+Alt+N）
11:09:16.369  自检 --dailytest(收藏)：id=dystopian-city-port Fav=True  索引里=True
11:09:17.486  自检 --dailytest(历史)：栈深=2
11:09:22.892  自检 --dailytest(回滚)：从 sleepy-rainy-evening 回到 dystopian-city-port  栈深=1
11:09:27.937  自检 --dailytest(结束)：库里 19 条 收藏 1 条 第一条fav索引=True
```

（19 条 = 原 17 条 + 导入 2 张测试图；测试痕迹已清掉，收藏也已取消。）

## 十二、两个 BUG + 收藏下线（11-12 11:20–11:40）

### 1. 「新下载的图设为壁纸，几秒后自己弹走」（严重，已修）

**日志铁证**：

```
11:20:39.281  设为静态壁纸：【不起眼女主角培育法丨加藤惠】…（第 9 张）  fit=10
11:20:45.775  动态壁纸启动：D:\Wallpaper\videos\dystopian-city-port.mp4 fps=30
```

设完静态壁纸 **6 秒后**，桌面又被换回上一张动态壁纸。

**根因**：`DoSet` 里只调了 `Dynamic.Stop()`，**没有把 `Config.DynamicEnabled` 置 false**。
看门狗每 4 秒跑一次，连续两次（约 8 秒）看到「动态开着、进程没了」，
就调 `RestartPlayer()` 按 `Config.LastDynamicId` 把旧片拉回来 —— 于是壁纸怎么都换不掉。

**修复**：`DoSet` 成功设上系统壁纸后，先停动态，再立刻
`Config.DynamicEnabled = false` + 存盘，并打一条日志
「回到静态壁纸，已关掉自动恢复（防止看门狗把旧动态壁纸拉回来）」。

**自检开关** `--statictest`：3s 开播 → 9s 设静态 → 24s 报告。

```
11:26:26.604  自检 --statictest(设静态前)：动态=True 播放器=True 待设=biliart-10928886-9
11:26:26.892  回到静态壁纸，已关掉自动恢复
11:26:41.890  自检 --statictest(15s后)：动态=False 播放器=False 窗口=False 看门狗重启=0
              当前壁纸=D:\Wallpaper\images\biliart-10928886-9.jpg
```

### 2. 「找不到云端生成按钮」

**原因**：入口只有两处 —— 卡片右键菜单、大图预览的第三个按钮。
但**卡片单击是直接设为壁纸**，用户根本进不到预览面板，右键菜单也不会主动去翻。

**修复**：顶栏常驻紫色按钮 **「✦ 云端动效」**（`MainWindow.CloudForCurrent`），
作用于当前选中的那张图：已有片子直接播，没有就下单。

顺带修了顶栏布局：收藏按钮原来挤在第 1 列，`搜索框(240) + 两个 chip + 按钮`
的总宽会溢出，压住左边的标题；现在收藏已下线，按钮组里只留
`✦ 云端动效 / 换一张 / 预览欢迎 / 刷新库 / 设置`，标题不再被压。

### 3. 收藏功能下线（按主人要求）

主人原话：「取消收藏功能，我想要的不是都下载到本地库了吗」。

删除范围：顶栏「☆ 收藏」按钮、卡片左上角星标、右键「收藏这张」、`favOnly` 过滤、
`Program.ToggleFav`、`FavInIndex`、`--dailytest` 里的收藏自检段。
只保留 `Models.Fav` 与 `index.json` 的 `fav` 键做向后兼容 —— 老索引还能正常读，只是不再有入口。

### 4. 场景动效核查结论（主人问「代码还在吗」）

**代码在，也确实在动**：`app/PlayerEffect.cs` 26KB，两个编译脚本里都在，
日志有「图片动效 场景」启动记录；`--fxvideo` 导出 4 秒实测全屏 42% 像素变化（>3），
地面 63%、天空 21%。

**但为什么「看着没效果」**：这张人物图被场景分析判成
**「整体漂移（不做分区位移，免得人物错位）」**，也就是整幅画面以极慢速度平移，
幅度只有 1~2px/秒，盯几秒确实看不出。对风景/海洋图它才会分区域动（云飘、浪涌）。

待主人定：加强漂移幅度 / 只在风景图暴露这个入口 / 直接删按钮。

### 5. 云端出片实战：加藤惠（biliart-10928886-9）

主人 11:24:45 自己从右键菜单下的单一直挂在队列里，11:37 出片：

```
11:37:24.016  云端生成：到货 biliart-10928886-9 → …\videos\biliart-10928886-9.mp4（447KB）
11:37:24.019  云端生成：到货处理 id=biliart-10928886-9  找到条目=True  要自动换=True
11:37:24.114  云端生成：换上新生成的飘动视频（…加藤惠…）
11:37:24.180  动态壁纸启动：…\videos\biliart-10928886-9.mp4 fps=30 mount=8
```

- 规格：1344×768、**3.00 秒**、447KB，无水印（左上角三帧放大核对，补得看不出痕迹）。
- 流程：VideoGen **最短 4 秒** → `video_fix.py --seconds 3 --no-loop` 裁到 3 秒。
  **注意**：3 秒的片子不能做「正放+倒放」，那样会变 6 秒、超出主人的 3 秒上限；
  `--no-loop` 就是给这种短片用的。
- 真机：桌面全屏 29.5% 像素变化，脸 14.1%，铺满无补丁、图标正常。

## 十三、功能补齐 + 一轮性能收拾（11-13 上午）

> 详细交接见 `docs/交接-2026-11-13.md`。这里只留结论和踩坑。

### 1. 先纠正一条错待办：播放器帧率没问题

11-12 记的「23fps → 30fps」是误判。实测：

- `biliart-17714251-4.mp4` 片源本身就是 **24fps**，日志 23fps ≈ 片源，不算掉帧；
- `dystopian-city-port.mp4`（1080p **30fps**）实测 **29–30fps**，解码管线是够的。

以后看帧率先 `ffmpeg -i` 确认片源帧率，别再优化解码。

### 2. 补完的功能

| 功能 | 关键实现 | 验收 |
|---|---|---|
| 拼音首字母搜索 | `app/Pinyin.cs`：GB2312 一级汉字按拼音排序，拿编码和 23 个声母边界比大小；二级汉字返回空 | `--searchtest`：城市夜景→CSYJ √ |
| 类型筛选（当天先做了色系+排序，下午按主人要求砍掉） | `MainWindow.FilterLocal` 一处收口 | 视频 16/16、静态 3/3、有飘动片 3/3 √ |
| 卡片状态角标 | 有飘动视频 → 「✦ 飘动」；正在桌面播 → 「▶ 播放中」 | `--uitest motion` 截图核对 |
| 设置：代理 | `Http.cs` 里新增 `Net`，替换 7 处硬编码 `Proxy = null` | `--configtest` 直连/host:port/带协议/坏地址 全 √ |
| 设置：缓存上限 + 立即清理 | `WallpaperFit.CacheBytes/TrimTo`，启动时按上限裁一次 | 设置窗口显示「当前占用 2 MB」 |
| 设置：配置导出/导入 | `ConfigStore.WriteTo / LoadFrom` 拆出来，导入前留 `.bak` | 往返字段一致 √ |

### 3. 性能与体验

- **缩略图 LRU 解码缓存**（160 张，键含 mtime）：以前每次 `RefreshList` 都 `OnLoad + IgnoreImageCache` 全量重解码。
- **日志按天归档**：跨天把 `wallpaper-debug.log` 改成 `wallpaper-debug-YYYYMMDD.log`，留 7 天。
  当前文件路径不变，`tail` / `Log.LastRun` / `Log.Tail` 都不用改。实测：把 mtime 改成昨天 → 启动后生成 `wallpaper-debug-20261112.log`。
- **搜索输入防抖 120ms**：清空操作仍立刻刷新（回本地图库、下载入库后清词都走这条），只有打字才等停手。

### 4. 踩坑

- **`build.bat` 失败时会 `pause`**：从脚本里跑必须 `cmd //c "build.bat > build-run.log 2>&1" < /dev/null`，
  否则命令一直挂着，看起来像编译超时。
- **新加的 .cs 必须同时加进 `build.bat` 的编译列表**，否则报「找不到类型」。
- **`MotionCapable` 对视频也返回 true**：拿它做「有飘动视频」筛选会把 16 个视频全混进来，要用 `HasCloudVideo`。
- **主窗口 Row 数从 4 变 5**（多了筛选条），overlay 的 `RowSpan` 也要跟着从 4 改 5。

### 5. 明确砍掉

- 电池降级 / 低电量保护：主人说「不做」，`app/Power.cs` 已删，配置里不留键。
- 库内去重（哈希 / pHash）：主人说「不要」，未实现。

## 十四、11-13 下午：筛选收口 + 欢迎语自动生成

### 1. 筛选条只留「类型」

主人先要「去掉排序，保留类型」，紧接着补一句「色系也不要，就留类型就行」。
现在筛选条只有 `全部 / 视频 / 静态 / 有飘动视频`，`sortMode`、`colorFilter`、`sortChips`、
`colorChips` 全部删除，`FilterLocal` 只做「类型 + 搜索」。

**注意**：`app/ColorTag.cs` 留着 —— 欢迎语生成在标题/分类都认不出时要用它兜底，
`--searchtest` 里那条断言已改名「取色（欢迎语兜底用）」。

### 2. 欢迎语改成「一张壁纸一句」

| 层 | 位置 | 说明 |
|---|---|---|
| 手工 | `resources\greetings.json` → `per_id` | 19 张各 2 句，对着缩略图里的元素写 |
| 自动 | `%APPDATA%\wallpaper\greetings-auto.json` | `WelcomeGen` 现写，新图入库就有 |
| 兜底 | `templates` + `who` | 库外条目、在线预览 |

自动生成的规则：标题关键词（霓虹/雨/海/林/星/桥/猫/火/城/几何/人物…19 组）→ 分类 → 色调，
再接一句收尾；避开库内已用句子，长度 ≤26 字。全本地，不联网。

三个入口都走 `Greeting.Ensure`：`Online.AppendIndex`（下载入库）、`SweepCloudJobs`（云端出片）、
`ReloadLibrary → Backfill`（图片扫目录收录）。**新增第四条入库路径时别忘了调它。**

### 3. 验收

- `--searchtest`：拼音/首字母/分类首字母/类型/取色 全 √ → PASS
- `--greettest`：专属文案 19/19 √、未收录回退 √、生成器三例 √、落盘往返（写→重读→清理）√ → PASS
- `--uitest`：筛选条只有一行「类型」，无「排序 / 色系」残留
- 真机模拟：新图丢进 `images\` → 日志 `欢迎语：为 1 张新壁纸自动补写` → auto 文件内容正确

### 4. 踩的两个坑

1. **JSON 转义**：auto 文件的 `_说明` 里手写了路径 `resources\greetings.json`，
   C# 字符串写成两个反斜杠 → 文件里变成单反斜杠 → 非法转义 → 下次启动整份读不回来。
   现在统一走 `Json.Quote`，并且 `LoadAuto` 解析失败会写日志（以前是静默吞掉）。
2. **删测试条目**：`RemoveAuto` 在删空后顺手把文件删掉，不留空壳；自检的落盘往返就靠这一步。

### 5. 首张真实下载的回检（同日 10:31，19→20 条）

主人真下载一张 B 站图后核对：自动补写**生效**（日志 + auto 文件都有），但暴露两点：

- **文案偏泛**：标题只有「朝」有画面信息，生成器原来不认「朝」→ 掉 nature 兜底「草木味」。
  已给 sunrise 组补 `朝/黎明/曙`，另加 `鸟居/神社`、`云海/雾` 两组；这张图因标题写不出画面，
  同时写进 `resources/greetings.json` 的手工层（鸟居 + 云海 + 星空 + 日出）。
- **删图不删文案**：删掉 `biliart-6710298-22` 后 auto 里留下孤立句子。
  已修 `Program.DeleteItem` → `Greeting.RemoveAuto`，新增 `--deltest` 端到端验证（PASS）。

`--searchtest` 的 `cs` 断言输出补了「多出的 N 张是标题首字母恰好含 cs」的说明：
新图标题「朝」+「手机壁纸」首字母正好是 `cs`，属正常命中，不是 bug。

---

## 附：文件地图

```
build.bat                     编译两个 exe + 替换 + 启动（唯一构建入口）
package.bat                   免安装打包 → dist\
app/Program.cs                单实例、托盘、轮换、ApplyItem、会话监听、欢迎触发
app/MainWindow.cs             界面全部（卡片/预览/搜索/图集/竖图过滤）
app/Bili.cs                   B 站：搜索 + 下载 + 专栏图集五路兜底解析
app/Online.cs                 motionbgs 解析、入库、缩略图、index 追加
app/LibraryStore.cs           读 index.json、扫描 images、ConfigStore
app/Settings.cs               设置窗口 + 开机自启（HKCU Run）
app/Dynamic.cs                播放器进程管理（启动/暂停/继续/停止/探测；视频与图片动效共用）
app/WallpaperPlayer.cs        独立播放器：桌面层挂载 + 探测 + 帧绘制
app/WallpaperFit.cs           竖屏/异形比例自适应（EXIF 方向、模糊底衬合成、缓存）
app/PlayerImage.cs            静态图动效入口（--image 模式）：0 场景动效 / 1 缓慢缩放 / 2 云端动效（仅占号，视频走播放器视频通道）
app/PlayerEffect.cs           场景动效：地平线分析 + 云/海/雨/灯光渲染（--fxdump / --fxvideo 可离线看）
app/CloudGen.cs               云端动效：工单（下单）/ 收货扫描 / 3 秒上限 / 提示词模板
tools/video_fix.py            出片后处理：去「AI生成」水印 + 裁到 3 秒 + 正放倒放无缝循环
app/Log.cs                    %TEMP%\wallpaper-debug.log（按天归档，留 7 天）
app/Pinyin.cs                 拼音首字母（GB2312 区位码，零依赖），本地搜索用
app/ColorTag.cs               缩略图主色 → 冷/暖/暗/亮，欢迎语兜底用
app/Http.cs                   HTTP 封装 + Net（全应用唯一代理入口）
app/Greeting.cs               欢迎文案（手工→自动→模板）+ 全屏淡入淡出窗口
app/WelcomeGen.cs             新壁纸的欢迎语生成（标题关键词 × 分类 × 色调，纯本地）
app/Desk.cs                   DPI + 设壁纸
app/Json.cs                   手写 JSON 解析
resources/greetings.json      手工欢迎语（per_id）+ 模板与主语池
resources/titles-zh.json      127 条润色中文标题
resources/app.ico             应用图标（tools/make_icon.py 生成）
docs/REQUIREMENTS.md          需求文档 v0.2
docs/readme.txt               打包随附的使用说明
docs/PROGRESS.md              本文件
```

## 十五、11-14：纳入版本控制 + 三项收口

### 1. Git 与「仓库瘦身」（主人指定：后续开发按标准流程）

- 项目原本没有 .git。新建 `main` 分支，身份沿用全局 Yan / 2389368803@qq.com。
- 本机 678MB / 923 个文件，99% 是可重建产物。**仓库只收源码 / 文档 / 工具 / 资源：82 个文件、1.06MB**。
- `.gitignore` 排除 `tools/out`、`demo`、`probe`、`dist`、`app/bin`、`*.log`、`*.bak-*`、`.workbuddy`；
  `.gitattributes` 锁 `*.bat / *.cmd / *.ps1` 为 CRLF（cmd 按 ANSI 读，LF 会让标签跳转解析出错）。
- **本机文件一个没少**：两个 exe、`app/bin/resources/` 三件套、618MB 产物全部保留。
  清掉的只有 `demo/`、根目录两个一次性大文件、4 个构建日志、`probe/` 里 32 个诊断截图；
  `tools/out` 未动。`build.bat` / `package.bat` 只依赖 `app/*.cs` + `resources/*` + `docs/readme.txt`。
- `build.bat --noboot`：只编译替换、不弹 GUI、不抢桌面，供 CI / 自检用；双击默认行为不变。
  **bat 不能带 BOM** —— cmd 会把 BOM 当成命令，导致第一行报错且 `@echo off` 失效、整份回显。

### 2. 导入/导出按钮：真实点击验证（`--importexporttest` 11/11 PASS）

交接第 9 节第 1 项。设置页两个按钮以前只验过 `WriteTo` / `LoadFrom` 往返，系统文件对话框从没点过。
现在加自检注入点（默认关闭、生产行为不变），`RaiseEvent` 真点 Click 处理器，验证：
导出落盘 → 覆盖前留 `.bak` → 新配置读回 → `Net.Configure` 同步 → 界面输入框刷新 →
点取消不覆盖 → 坏文件不抛异常 → 零污染（真实 `config.json` 逐字节未变）。

**顺带修掉两个真实缺陷**（`app/Settings.cs` 的 `ImportConfig`）：
1. 坏文件让 `JavaScriptSerializer` 直接抛异常，原来没人接 → 未处理异常；
2. `{"a":1}` 这类合法 JSON 会被接受，`LoadFrom` 把缺字段全填默认值 →
   库路径 / 代理 / 轮换等设置被静默清空。现在要求解析成功且带非空 `libRoot`
   （`ConfigStore.WriteTo` 必定写它）。

### 3. 代理真实链路（`--proxytest` 9/9 PASS）

交接第 9 节第 2 项。新增 `app/MockProxy.cs`（本机 TCP 代理，零外部流量、零费用），
让应用内 `HttpWebRequest` / `WebClient` 真的经代理走一趟：
- 代理收到绝对 URI 请求行 `GET http://wallpaper-proxy-check.invalid/probe HTTP/1.1`（走代理的客观证据）；
- `Host` 指向目标域名；`WebClient`（`GenApi.Download` 那条路）同样走代理；
- 本机 `127.0.0.1` 绕过代理（`BypassProxyOnLocal`），`--cloudflowtest` 的模拟服务端不受影响；
- 空配置直连、代理零请求、`Net.Proxy == null`（老行为红线）。

### 4. 「最后一张壁纸有视频却要我生成」—— 根因与修复

日志实锤：21:39:49 播放器重开解码器报 `0x80070002`（文件不存在），6 种打开方式全失败；
21:40 下单时 `CloudGen.Enqueue` 才会放行（它见到主文件已不在），21:40:12 取消时
`CancelCloud` 又把这条的 `motion/fx` 清成静态 → 于是变成「要重新生成」。
`LibraryStore.Delete` / `Program.DeleteItem` 都**不碰** `videos\`，不是删 `biliart-10096573-7` 误删的；
主文件是在播放途中被外部移走 / 删除的。应用只认精确的 `videos\<id>.mp4`，
主人看到的 `.bak` / `.bak-v2` 它一律不认 —— 这才是「文件夹里有视频」的误会来源。

**修复**：
- `Program.VideoFor` 加自愈：主文件缺失时找 `<id>.mp4.bak*`，取 mtime 最新的一份
  `File.Copy` 回主文件（备份保留）并写日志。新增 `--videotest`（7/7 PASS，隔离临时库）。
- 真实库已自动恢复 `biliart-17714251-4.mp4`（3 秒 / 466KB，来自 `.bak-v2`），
  `--searchtest` 的「有飘动片」从 2/2 回到 **3/3**。
- `index.json` 里该条的 `motion` / `fx` 恢复成 `true / 2`（21:39 主人的选择），
  原文件备份为 `D://Wallpaper//index.json.bak-20261114-videofix`。

### 5. 回归（全绿）

`build.bat --noboot` 零输出；`--searchtest` / `--greettest` / `--deltest` / `--configtest` /
`--cloudcfgtest` / `--cloudflowtest` / `--arrivetest` 全 PASS，`--genapicheck` / `--uitest` /
`--settingstest bottom` 照常出图；新增 `--importexporttest` / `--proxytest` / `--videotest` 全 PASS。
库 `index.json` 19 条（主人 21:38 删了 `biliart-10096573-7`，界面含散图共 20 张）、
无 `zz-` 残留、队列空、真实 config 未改。
`--cloudtest` 仍未跑：它会往真实队列写一张 2 天超时的死工单，没有真实出片通道时跑了只留垃圾。

### 6. 新增 / 改动文件

`app/MockProxy.cs`、`app/SelfTestExtra.cs`（新）；`app/Program.cs`（三个自检分发、`VideoFor` 自愈）、
`app/Settings.cs`（按钮字段 / 注入点 / 导入校验）、`build.bat`（编译列表 + `--noboot`）、
`.gitignore` / `.gitattributes`（新，已在初始提交）、`docs/readme.txt`、`package.bat`、`docs/PROGRESS.md`。

### 7. 下次先看

1. 远程仓库地址还没定（GitHub 账号 `dingdingdang460` 下无公开仓库）；本地提交已完成，
   拿到 URL 后 `git remote add origin <url>` + `git push -u origin main` 即可。
2. 真 Key 链路仍未验证（本机没填服务商 / Key，涉及额度，不代填）。
3. `--cloudtest`（会写真实工单）、4K / 多屏、循环首尾 ping-pong、
   断点续传 / yt-dlp 侧车 / 时间段轮换 / 锁屏同步，未动。

---

## 十六、11-14 续：相关视频「一律复用，绝不重新花钱」

### 1. 主人的要求

> 「这个问题要修复下，只要的关联的文件夹里有相关图片的视频就应该播放，
> 而不是让我重新生成浪费钱」

上一节做的 `.bak*` 自愈只是特例。主人要的是**通用规则**：库里只要存在跟这张图
有关的片子，就直接播；任何情况下都不许因此再下一次单、再花一次额度。

### 2. 新增 `app/RelatedVideo.cs`：统一发现器

只读本机文件、零联网、零费用，按优先级找：

| # | 摆法 | 例子 |
|---|------|------|
| 1 | 规范主文件 | `videos\<id>.mp4` |
| 2 | 旧片备份 | `videos\<id>.mp4.bak` / `.bak-v2` |
| 3 | 以 id / 图片 stem 开头的变体 | `<id>.raw.mp4`、`<id>-v2.mp4`、`<id>.mp4.part` |
| 4 | `videos\` 下一级关联子文件夹 | `videos\_raw\<id>.raw.mp4`；文件夹本身以图命名时，里面**任何**片子都算 |
| 5 | 图片同目录 / `images\` / `thumbs\` / 库根 | 跟图片同 stem 的 mp4 |
| 6 | 云端工单 `out` 字段 | `jobs\{queue,done,fail}` 里 id 或 image 对得上、且 out 还在库内的片子 |

- 名称匹配要求 id 后面跟分隔符（`. - _ 空格 ( [`），避免 `<id>` 误吃到 `<id>0` 这种别的壁纸。
- **只在当前 LibRoot 范围内认片子**：换库 / 自检用临时库时不会把上一个库的视频串进来。
- 找到但不在规范位置时，`File.Copy` 一份到 `videos\<id>.mp4`（原文件保留、日志写明来源），
  这样 `CloudGen.StateOf` / `SweepCloudJobs` / 播放器这些只认规范路径的老逻辑一行都不用改。
- 目录清单带 2 秒缓存 + `Invalidate()`，界面每次刷新问 19 张图不会反复扫盘。
- **认不出的边界**：文件名跟图片毫无关系、又没有工单记录的散片（库里那两个
  `Anime_illustration_of_a_girl_w_*.mp4` 就是），程序没法判断它属于哪张图。
  改名为 `<id>*.mp4` 或放进 `videos\<id>\` 即可，不必重新生成。说明已写进随包 readme。

### 3. 下单前的四道闸（花钱的是 `GenApi.Submit`）

`app/Program.cs` / `app/CloudGen.cs` 全部收口：

1. `GenCloud` —— 有片直接 `SetMotionFx(it,2)` 播放，**连确认下单浮条都不弹**；
2. `StartCloud` —— 浮条亮着时片子才落盘（外部生成器 / 手工复制）也不会重复提交；
3. `QueueCloudGen` —— 所有下单路径的汇聚点，先查 `VideoFor`，有片返回 false 并复用；
4. `CloudGen.Enqueue` —— 写工单前最后一道；`RunApiJob` 开头再加一道兜底，
   真要 POST 之前再看一眼有没有现成片子。

配套：`CloudGen.StateOf` 也按「有没有相关视频」判定，菜单才会显示
「云端动效：播放这段视频」而不是「云端生成动效视频（3 秒）」；
`CancelCloud` 不再因为主文件缺失就把动效标记清回静态；
`SweepCloudJobs` 会把落在别处的片子认作到货，工单不白挂到超时。
`--cloudflowtest` 第 0 步改用内存里的假 id（真库里的静态图现在都有片子了），
保住「没配 Key → 去设置、不写假工单」这条断言。

### 4. `--relatedvideotest`：56/56 PASS，零下单、零费用

隔离：`ConfigStore.DirOverride` + `LibRoot` 指向临时目录 + `App.SelfTestNoPlay`（不换桌面）。
**反证设计**：故意把服务商 / 假 Key 配好、把 `GenApi.EndpointOverride` 指到本机
`MockCloudServer`，所以只要任何一道闸漏了，`RunApiJob` 就会真的 POST 一次。
断言 7 种摆法下：认得出 → 归一到规范主文件且内容一致 → 三个下单入口全拒绝 →
工单队列没被写 → `mock.SubmitCount` 恒为 0 → 没弹确认浮条 → `StateOf=Ready`。
再验两条反例：无关视频 / 同前缀别的 id 不误认；**完全没有相关视频时才允许下单**。
收尾验真实 `index.json` 逐字节未变、真实队列数量不变、配置与端点全部还原。

### 5. 回归（全绿）

`build.bat --noboot` 编译零输出（`build.log` / `build-player.log` 均 0 字节）；
`--relatedvideotest 56/56`、`--videotest 7/7`、`--importexporttest 11/11`、
`--proxytest 9/9`、`--searchtest`（有飘动片 3/3）、`--greettest 19/19`、
`--deltest`、`--configtest`、`--cloudcfgtest`、`--cloudflowtest 13 项`、
`--arrivetest`、`--menutest`、`--genapicheck`、`--uitest`、`--settingstest bottom` 全 PASS。
现场：真实库 19 条、无 `zz-` 残留、队列空、临时目录 0 个、真 Key 仍为空。
最后一张 `biliart-17714251-4.mp4`（466KB）在盘、`motion=true / fx=2`。

### 6. 版本与改动文件

`package.bat` / `docs/readme.txt` 升到 **v0.7**，重新打包
`dist\WallpaperManager-v0.7\`（`dist/` 仍被 `.gitignore` 排除，只留本机）。

本轮新增 `app/RelatedVideo.cs`；改动 `app/Program.cs`（VideoFor 换实现、
四道闸、CancelCloud、SweepCloudJobs、自检分发）、`app/CloudGen.cs`（Enqueue / StateOf）、
`app/SelfTestExtra.cs`（RelatedVideoGuard + 复用 VideoRecovery 的缓存失效）、
`build.bat`（编译列表）、`docs/readme.txt`、`docs/PROGRESS.md`、`package.bat`。
按主人确认的标准流程整体纳入版本控制并编译自检通过后提交。

---

## 十六、11-14 晚：假「生成中」、工单不可见、没带 id 的散片（v0.8）

主人连续三问：「video 文件夹下有视频啊，怎么最后一张还要生成？」「生成功能也还不行吗？
点完就显示生成中，用的谁家 api？缺信息该弹窗提示啊，就一个生产中还是假的」
「我下单在哪能看到？还是就你现在能看到？」

### 1. 现场诊断（只读日志 / 文件 / 配置）

- 22:36:19 v0.7 确实从 `.bak-v2` 恢复了 `biliart-17714251-4.mp4`，播放器解码成功
  （1600x900 / 29fps）；22:36:51 主人手动关掉动态播放 → `motion=false, fx=2`。
- 22:37 左右 `videos\` 目录被改动：主文件和两个 `.bak` **全部消失**。
- 22:37:35 主人点生成 → `VideoFor` 按名字找不到任何相关片 → 四道闸全部放行，
  **真下了一单** `jobs\queue\biliart-17714251-4.json`（至今还挂着）。
- 主人看到的「文件夹里有视频」是 3 个 `Anime_illustration_of_a_girl_w_*.mp4` /
  `Anime_girl_with_short_dark_bob_*.mp4` —— 云端服务商原始出片文件名，没带 id，
  旧规则明确把它们列为「认不出的边界」。
- `config.json`：`genProvider="" / genApiKey="" / genModel=""`。
  两家可选（通义万相 DashScope `wan2.6-i2v-flash` 约 ¥0.15/秒、
  火山方舟 Ark `Doubao-Seedance` 约 ¥0.2~0.6/秒）**一家都没用**，
  22:37:35 后没有任何 HTTP 提交记录，**没扣过钱**。
- 「生成中」是假状态：`QueueCloudGen` 无条件写工单，只有 `GenApi.Ready()` 为真
  才起 `RunApiJob`；没配好时工单要挂 2 天超时，界面却显示「☁ 生成中」。
  卡片「动态播放」走 `SetMotionFx` 直接调 `QueueCloudGen`，绕过了会提示「去设置」的浮条。
- 工单界面上完全看不到，只有磁盘上的 JSON；界面没有查看入口。

### 2. 修复

1. **消灭假状态**：`App.AskCloudConfig()` 在 `QueueCloudGen` 总闸处检查
   `GenApi.Ready(Config)`，没配好就弹窗列明缺什么（服务商 / Key / 模型、两家单价、
   工单目录），给「去设置」；**不写工单、不改动效标记、不报 Queued、不扣费**。
   自检用 `SelfTestNoDialogs` / `SelfTestCloudAnswer` 注入。
2. **历史僵尸单不再冒充生成中**：`CloudGen.State` 新增 `Manual`；
   `StateOf` 在没配 API 时返回它。卡片角标改「⏸ 待处理」、菜单改「有一张不会自动出片
   的旧工单（点此查看）」，新增 `MainWindow.CloudManual()` 浮条，明确写明不会自动出片。
3. **工单可见**：浮条新增「查看工单」按钮 → `App.OpenJobFolder()` 用资源管理器打开
   目录并选中当前 json；下单后状态栏直接给出工单完整路径；`CloudChannel()` 带工单目录；
   「怎么生成？」ToolTip 也写了路径。
4. **没带 id 的原始出片也能复用**：`RelatedVideo` 加第 7 层 `FromOrphans()` ——
   只认名字带 UTC 时间戳、且不匹配任何已知 id（历史工单 id + 库内图片 stem）的散片，
   按该图下单时间就近配对、配不上取最近出的一片，进程内一份只归一张图。
   **纯读**：直接返回原路径播放，不复制、不改名、不往壁纸库写文件，日志写明来路。
   `zz-` 前缀（项目自检假条目）跳过这一层，避免 `--arrivetest` / `--cloudflowtest`
   被真实库散片误拦。反例照样不误认：无关 mp4、同前缀别的 id 都不算。
5. 顺手修 `SweepCloudJobs`：以前 `VideoFor` 找到别处片子后又把 `vid` 赋回规范路径，
   等于什么都没做；现在直接用 `VideoFor` 返回的路径判到货。
6. 界面刷新每秒问好几次，推断关联的日志改成只在第一次认领时写一行，不刷屏。

### 3. 自检与回归（全绿）

新增 `--clouddialogtest` **13/13 PASS**（弹窗内容、零工单、`State.None`、
动效标记不变、僵尸单判 `Manual`、真实队列数不变）；
新增 `--orphantest` **13/13 PASS**（散片认出 → 三个下单入口全拒绝 → `SubmitCount=0`
→ 纯读不写盘 → 两条反例不误认 → 无片才允许下单 → 真实 index.json 逐字节未变）；
`--relatedvideotest 56/56`、`--videotest 7/7`、`--importexporttest 11/11`、
`--proxytest 9/9`、`--searchtest`（有飘动片 3/3）、`--greettest 19/19`、
`--deltest`、`--configtest`、`--cloudcfgtest`、`--cloudflowtest`、`--menutest`、
`--genapicheck`、`--uitest`、`--settingstest bottom` 全 PASS。
`build.bat --noboot` 两个编译日志均 0 字节。
`--arrivetest` 本轮**跳过**：它会在真实壁纸库造 `zz-` 测试图片/视频，按权限边界不跑。
真实库最后一张已经在日志里被认出来并关联到
`videos\Anime_illustration_of_a_girl_w_2026-11-12T10-02-47.mp4`。

### 4. 权限边界事件（如实记录）

本轮用 `TEMP` / `TMP` 重定向到仓库内 `.selftest-tmp\`（已加进 `.gitignore`），
让日志与临时文件全落在工作空间内。但 `--deltest` / `--greettest` / `--configtest`
仍会走真实库：快照对比显示 `D:\Wallpaper\index.json`、`index.json.bak`、
`%APPDATA%\wallpaper\config.json`、`greetings-auto.json` 共 4 个文件的 **mtime 被重写**
（22:58:13）。内容已核验全部还原：index 仍 19 条、无 `zz-` 残留、
`index.json.bak` 与 `index.json` 内容一致、真 Key 仍为空、队列文件未被删改。
**以后这三个自检要么进一步隔离、要么跑之前先问主人**，不能默认跑。

### 5. 版本与待办

`package.bat` / `docs/readme.txt` 升 **v0.8** 并重新打包。
`22:37:35` 那张假工单已由程序按新规则自动判为到货，从 `queue\` 移到
`%APPDATA%\wallpaper\jobs\done\biliart-17714251-4.json`（**没有删除**），
同时把最后一张的 `motion` 恢复成 `true / fx=2`。现场：`index.json` 19 条、
无 `zz-` 残留、`queue\` 空、真 Key 仍空。这是修好的 `SweepCloudJobs` 在应用启动时
自己跑出来的收货逻辑，不是助手手动改仓库外文件；主人若想彻底清掉 `done\` 里
那张 JSON，说一声即可。
远程仓库 URL 仍缺，未 push；真 Key 链路未碰。

## 十七、11-14 深夜：按画面区分图片与视频（v0.9）

### 1. 主人三连问与本轮红线

1. 「刚下载了一张新壁纸，为啥点击动态会自动切换到别的壁纸视频」；
2. 「搜索框旁边的飘动视频现在不是没有了吗」（筛选条「有飘动视频」被宽口径污染）；
3. 「云端生成功能按钮怎么没有了」。
随后追加两条硬要求：**「不要就近认领……你要做到区分图片与视频是否为同一张图片背景」**、
「这个工作目录下完全权限，把需求一股气做完」。仓库外仍保持只读、改动前先问。

### 2. 根因（日志实据）

23:11:34 新图 `biliart-27606684-1`（23:11 刚下载）被 **v0.8 的 `FromOrphans()`**
按「出片时间就近认领」自动挂到 `Anime_illustration_of_a_girl_w_2026-11-12T02-18-26.mp4` ——
那片其实和 `videos\_raw\biliart-17403571-4.raw.mp4` 同为 2,388,693 字节，是**别的壁纸**的片子。
`HasCloudVideo()` 误判 true → 顶栏「✦ 飘动视频」直接 `SetMotionFx` → 播错片，
并把 `motion=true / fx=2` 写进 index.json。同一时刻 `biliart-20124447-14` 也被误认到另一段散片。
另外旧版把「有片」时右键菜单 / 大图预览的按钮整个换成「播放这段视频」，
`it.Motion=true` 时甚至换成「改回静态壁纸」，导致「云端生成」入口彻底消失；
筛选条「有飘动视频」用同一个 `HasCloudVideo`，于是新图也被算进这一档。

### 3. 修复：内容判定，不再按名字或时间猜

- 新增 `app/VideoFingerprint.cs`，复用项目自带的 `app/MfVideo.cs`（Media Foundation
  SourceReader）抽视频第一帧，**零新依赖、零联网**：
  - 图片走 WPF `BitmapImage` → `WriteableBitmap`，视频首帧走 `MfVideo.Reader.NextFrame`；
  - 两边都缩到 33×32 灰度 → z-score 归一化（消亮度/对比度差）+ 8×8 dHash（消压缩差）；
  - 距离 = 归一化平均绝对差 MAD + 64 位汉明距离，**两个同时达标**才算同一画面；
  - 按「路径 + mtime + size」缓存指纹，界面刷新不会反复解码。
- `app/MfVideo.cs` 加 `Quiet` 开关，抽帧时静音 `[MF]` 诊断日志（播放器进程不受影响）；
  `build.bat` 源码列表补 `VideoFingerprint MfVideo`。
- `app/RelatedVideo.cs` 第 7 层删掉 `FromOrphans / JobCreatedFor / Epoch / StampRe / IsOrphan`
  整套时间猜测，改成 `EnsureContentMap()`：
  `KnownKeys` 排除名字已明确归属的片子 → 全库算「每张图 × 每段未归属视频」距离矩阵
  → 按 MAD 升序贪心一对一分配（一段片子只归一张图）→ `ContentOwner()` 查表。
  命中后**纯读直接播原文件**，不复制、不改名、不往壁纸库写文件。
- **阈值用真实库标定**（`--contenttest` 输出的距离矩阵）：
  同一画面 MAD `0.067 / 0.102 / 0.256`、hash `6 / 8 / 12`；
  不同画面 MAD 最低 `0.781`（多数 > 0.9）、hash 最低 16 但 MAD 都 > 1.1。
  取 `MaxMad=0.45`、`MaxHashDist=18`，正负之间留足安全带。
  三段散片各自归回真正对应的图：
  `02-18-26 → biliart-17403571-4`、`10-02-47 → biliart-17714251-4`、
  `03-37-08 → biliart-10928886-9`；新图 `biliart-27606684-1` 判 **False**。
  按名字明确归属的 1~6 层逻辑保持原样不变。

### 4. 「云端生成」入口恢复 + 显式重新生成

- 新增 `App.RegenCloud(it)`：弹确认说明「会再花一次钱、旧片留备份」→
  `StartCloud(force)` → `QueueCloudGen(force)` → `CloudGen.Enqueue(force)`。
  force 只跳过「有片就播」，**绝不跳过**「没配 API 不许写假工单」总闸；
  `AskCloudConfig(it, regen, ...)` 文案区分「重新生成一片需要先配好」。
  `Enqueue(force)` 先把旧片复制成 `.bak-regen-<时间戳>`，出片时覆盖规范路径；
  `CloudGen.StateOf` 改为队列里有新单时优先报 `Queued`，避免重新生成后界面毫无变化。
- `MainWindow.cs`：
  - 右键菜单有片时同时给「云端动效：播放这段视频」和「✦ 重新生成一片（会重新计费）」；
  - 大图预览按**有没有片**（而不是 `it.Motion`）决定第二按钮，有片时第三按钮是重新生成，
    没片时第二按钮永远是「✦ 云端生成（3 秒）」，误标动态的图仍可从第三按钮回退静态；
  - 预览 meta 不再对「标了动态但一片都没有」的图谎称「已设为动态播放」，
    改成明确提示「标了动态但没找到片子（点「云端生成」）」；
  - 顶栏「✦ 飘动视频」ToolTip 指明重新生成入口。
- 筛选条「类型 → 有飘动视频」用同一套内容判定，`--searchtest` 实测
  **静态 4/4、有飘动片 3/3**（新图不再被误算）。

### 5. 自检与回归（全绿）

新增 `--contenttest` **5/5 PASS**（正/负样本可区分、同图全认异图全不认、
画面不匹配的图一律判无片、现场判定矩阵）；
`--orphantest` 改用真实图片/视频只读复制到隔离库，**16/16 PASS**（含「画面不同的新图绝不被认领」）；
`--clouddialogtest` **20/20 PASS**（新增取消重新生成、force 仍要配 API、不写工单不扣费）；
`--relatedvideotest 56/56`、`--videotest 7/7`、`--importexporttest 11/11`、
`--proxytest 9/9`、`--searchtest`（静态 4/4 有飘动片 3/3）、`--cloudcfgtest`、
`--cloudflowtest`、`--menutest`、`--genapicheck`、`--uitest`、`--settingstest bottom` 全 PASS。
`build.bat --noboot` 两个编译日志均 0 字节。

**按权限边界跳过**：`--deltest / --greettest / --configtest / --arrivetest`
（这四个会往真实库或真实配置写文件）。仓库外 74 个文件做前后快照对比，
**差异 0 处**（内容连 mtime 都没变）；`TEMP/TMP` 全落在仓库内 `.selftest-tmp\`。

### 6. 版本与待办

`package.bat` / `docs/readme.txt` 升 **v0.9** 并重新打包。
**需要主人授权的一件小事**：新图 `biliart-27606684-1` 在 `D:\Wallpaper\index.json` 里
还留着上一版误写的 `motion=true / fx=2`（仓库外文件，未擅自改）。
现在程序已按内容判定不播错片，界面也会提示「标了动态但没找到片子」并给
「改回静态壁纸」按钮；要我把这条索引标记清成静态，说一声即可。
远程仓库 URL 仍缺，未 push；真 Key 链路未碰。

## 十八、v0.9.1：切换壁纸后「▶ 播放中」不再定格（11-15 凌晨）

主人反馈：「怎么我切换了壁纸，上一个壁纸的动态视频还显示播放中？」

### 1. 根因（三处叠加）

- `MainWindow.BuildCard` 把 `playing = DynamicEnabled && LastDynamicId == it.Id`
  **在建卡时算一次就写死**。切换壁纸只调 `SetCurrent()`（改边框和状态栏），
  从不 `RefreshList()`，于是旧卡片上的「▶ 播放中」一直定格到下次重建列表。
- `DynamicEnabled` / `LastDynamicId` / `cursor` / 角标 / 托盘各写各的：
  `DoSet` 只把总闸置 false、**不清 `LastDynamicId`**；`StartDynamic` 失败分支
  只弹一句状态栏，播放器、总闸、旧 id 全都不动 —— 切到一张播不了的新图时，
  桌面继续放上一张的视频、界面继续写上一张「播放中」。
- `Dynamic.Launch` 的交接模式在**新窗口没上屏**时也 `return true`，
  上层照样写「正在播新图」的假状态。

### 2. 修复

- `App` 新增唯一收口 `MarkPlaying(it)` / `MarkStopped()` / `IsPlaying(it, alive)`
  / `IsPlayingNow()` / `PlayingNow()` / `StopPlayback()`。
  `MarkStopped` 同时关总闸 + **清空 `LastDynamicId`**（以前留着旧 id，
  看门狗或下次启动一旦开闸就把旧片拉回来）。所有路径改走收口：
  `StartDynamic` 成功/失败、`StopDynamic`、`ToggleMotion(false)`、
  `ApplyItem(allowMotion=false)`、`DoSet` 成功/失败、`DeleteItem`、
  看门狗连续 5 次重启失败。启动失败现在会 `StopPlayback()` + 清状态 +
  明确提示「已停掉上一张的视频」，设壁纸失败也兜底收播放器。
- `App.SelfTestNoPlayerOps` / `SelfTestAliveOverride` 只给隔离自检注入，
  生产行为不变。
- `MainWindow`：每张卡**常驻**创建状态角标（默认隐藏）并登记到
  `stateBadges`；`BadgeInfo` 缓存 hasVid / queued / manual（这些只在
  `RefreshList` 里变）；新增 `RefreshBadges()` 只重算文字/颜色/可见性，
  不重建卡片、不解码图片、不碰磁盘。`RefreshList` / `SetCurrent` /
  `MarkPlaying` / `MarkStopped` 都触发刷新；`WatchdogTick` 每 4 秒再对一次齐。
  `playing` 判定加一道**播放器进程必须真活着**的保险：配置残留但进程已死，
  绝不显示假的「播放中」。托盘菜单与悬浮提示同样改用 `IsPlayingNow()`。
- `Dynamic.Launch`：窗口 2.5 秒内没上屏就如实 `return false` + 明确 err，
  顺手收掉空壳进程；窗口检查与旧播放器的交接顺序不变。

### 3. 验证（全绿）

- `build.bat --noboot`：`build.log` / `build-player.log` 均 0 字节。
- 新增 `--badgetest` **28/28 PASS**：全隔离复现「开播 A → 切静态 →
  切到 C 开播 → 新片启动失败 → 配置残留但进程已死 → 进程回来角标恢复」，
  逐项断言角标文字、`DynamicEnabled`、`LastDynamicId`、状态栏、托盘文案。
- 新增 `--launchtest` **11/11 PASS**：真跑一次播放器（只读库里一个 mp4
  试播约 1 秒），确认窗口正常上屏时 `Start` 仍返回成功、进程/窗口都在、
  `Stop` 后 `IsPlayingNow` 立刻变假、无残留进程。配置隔离到仓库内临时目录，
  不设系统壁纸、不改真实 config。
- 既有隔离回归全部 PASS：`--searchtest`（静态 4/4、有飘动片 3/3）、
  `--importexporttest 11/11`、`--proxytest 9/9`、`--videotest 7/7`、
  `--relatedvideotest 56/56`、`--orphantest 16/16`、
  `--clouddialogtest 20/20`、`--contenttest 5/5`。
- 权限边界：`D:\Wallpaper\index.json`、`%APPDATA%\wallpaper\config.json`、
  `jobs\queue` 前后快照**差异 0 处**；`D:\Wallpaper` 67 个文件、
  280,056,115 字节不变；`videos\` 无任何新写入；跑完无残留播放器/管理器进程。
  按既有约定仍跳过 `--deltest / --greettest / --configtest / --arrivetest`。
  自检按程序既有行为追加 `%TEMP%\wallpaper-debug.log` 运行日志。

### 4. 版本

`package.bat` / `docs/readme.txt` 升 **v0.9.1**，重新打包
`dist\WallpaperManager-v0.9.1.zip`。待办不变：远程 URL、`index.json` 里
新图误写的动效标记、`jobs\done\` 假工单、真 Key 链路，均按权限边界等主人授权。

## 十九、11-15：WorkBuddy 免费模型能不能用于云端生成（v0.9.2）

主人问：「云端生成那里选定模型不能用 WorkBuddy 里配的吗，我看 hy4 和 hy3
都是免费的」。核查结论：**不能**，且不是少加两个下拉项的问题。

### 1. 依据（官方文档 + 官方公告，2026-11-15 核实）

- `docs.workbuddy.cn/.../Function-Description/Model`「内置模型」表：
  Hy4 preview = 混元新一代模型，可用功能「√ 图片输入 / √ 思考模式」；
  Hy3 = 混元思考模型，同样只有图片输入与思考模式。它们是**对话 / 推理模型**，
  输出文字，不产出视频文件。同页「自定义模型」说明：配置（含 API Key）只保存在
  本地 `workbuddy/models.json`，WorkBuddy 仅作通信链路转发对话。
- 官方 Hy4 上线公告补充：①「Hy4 是一个具备视觉理解能力的大语言模型，
  如果用户有特定生成图片与视频的任务，会切换到相应多模态模型完成，因此会按照
  正常规则产生积分消耗」；② Hy4 / Hy3 限免到 **2026-11-15 23:59**，免费用户
  每日赠额度，触及后消耗积分。即「免费」只覆盖对话额度，不覆盖出片。
- 本程序 `GenApi.cs` 走的是 DashScope `video-synthesis` / Ark
  `contents/generations/tasks`：**提交图 → 拿 taskId → 轮询 → 下载 mp4**，
  与 `/chat/completions` 不是同一种协议；`model` 必须是图生视频模型 ID。
- 独立 exe 无法读取 WorkBuddy 客户端的模型会话额度或凭证，
  设置面板里的 Key 是主人在阿里云百炼 / 火山方舟自己开通的 Key。

因此**没有**把 hy4/hy3 加进 `GenModels`：加进去只会造成「选了免费、点了没片」的
假期待，正是主人此前反复反对的假状态。

### 2. 改动：把 WorkBuddy 积分做成**默认出片通道**

主人追加两条：「就是现在云端生成，我想优先消耗 WorkBuddy 里的积分，使用这里的模型
图生视频」「直接做成完整可用的功能，而不是只给我一个页面和点击之后没有任何反应的按钮」。
所以把旁路提升为默认通道，按秒付费降级为可选项，一次点击要有真实动作与产物凭据。

- `Models.cs` / `LibraryStore.cs`：新增 `AppConfig.GenChannel`（`buddy` 默认 / `api`），
  落盘 `genChannel`；老配置缺字段时 Load 回填 `buddy`。
- `Settings.cs`：加「出片通道」下拉（默认「优先 WorkBuddy 积分」），
  Save / LoadFrom 双向绑定；醒目说明用 `CloudGen.BuddyNote`。
- `CloudGen.cs`：
  - `BuddyDir` = `jobs\buddy\`，与 `queue\` **分开**：queue 是应用直连服务商、
    后台线程会自己提交的单；buddy 是等 WorkBuddy 会话确认积分后才可能出片的单。
    分开后「没配 API 绝不写假工单」的既有断言依然成立，也不会被 `RunApiJob` 误提交。
  - `WriteBuddyJob` / `RemoveBuddyJob` / `BuddyPendingCount`；工单含
    `channel=workbuddy-credits`、`status=waiting-for-workbuddy`、源图、`out`、秒数、提示词。
  - `State.BuddyPending`；`StateOf` 有片优先 Ready，其次才判待领，绝不冒充「生成中」。
  - `BuildBuddyRequest()` 纯函数生成可粘进 WorkBuddy 会话的图生视频指令
    （源图、目标 `videos\<id>.mp4`、3 秒 / 720P / 无水印 / 镜头静止 / 首尾循环、
    先报积分等确认、已有同画面片子别重出）。
- `Program.cs`：
  - `IsBuddyChannel()`；`StartBuddyGen(it, regen)` 是「云端生成」默认动作：
    **弹窗告知积分 → 登记待领工单 → 指令进剪贴板 → 角标变「⏳ 等 Buddy 出片」**。
    三选（是=积分出片 / 否=改用按秒付费 / 取消=什么都不做）；
    取消与改用付费都不登记工单。只写 `pendingApplyId` 内存标记，不写 config。
  - `GenCloud` / `RegenCloud` 默认走这条；`GenChannel="api"` 才回到原 `CloudChoose`
    → `QueueCloudGen` → `GenApi` 直连（含 `AskCloudConfig`），**原付费链路未改**。
  - `SweepBuddyJobs()` 挂在 `SweepCloudJobs()` 前面：mp4 落进 `videos\` 后自动收掉
    待领工单、标动态、自动换桌面、刷新角标；自检注入 `SelfTestNoPlay` 时不碰系统壁纸。
  - `OpenBuddyFolder()`：右键可直接打开待领工单目录（延续「我下单在哪能看到」）。
- `MainWindow.cs`：`BadgeInfo.BuddyPending` + `ApplyBadge` 显示「⏳ 等 Buddy 出片」；
  待领时右键改为「重新复制指令」，并加「查看 WorkBuddy 待领工单」。
  另有手动补充入口「⧉ 复制交给 WorkBuddy 的指令（不填 Key）」。

**边界（必须如实说明）**：桌面 exe 拿不到 WorkBuddy 客户端的模型会话凭证，
所以不能像填 Key 那样在进程内直接扣积分出片。真正出片由 WorkBuddy 会话执行
（手动粘贴指令 / 客户端定时自动领单）；程序负责下单登记、状态可见、到货自动收货播放，
绝不假装在生成，也绝不偷偷扣积分。

### 3. 验证（全绿）

- `build.bat --noboot`：`build.log` / `build-player.log` 均 0 字节。
- `--buddyhinttest` **43/43 PASS**：除口径断言（限免期限、每日额度、
  「生成不了 mp4」「不共享额度/Key」「积分」、模型目录不混入 hy4/hy3、指令要素、
  秒数夹到 3 秒、不含服务商地址/Key）外，真跑默认通道整条链：
  默认通道判定、设置下拉默认项、点生成弹积分告知、登记待领工单、
  工单字段、**不写 queue 工单**、指令生成（自检不写系统剪贴板）、
  状态为 `BuddyPending`、角标文字「⏳ 等 Buddy 出片」、
  取消/改用付费都不登记工单、造一份到货 mp4 后自动收单并转 Ready。
- 既有 10 项隔离回归全 PASS：`--searchtest`（静态 4/4、有飘动片 3/3）、
  `--importexporttest 11/11`、`--proxytest 9/9`、`--videotest 7/7`、
  `--relatedvideotest 56/56`、`--orphantest 16/16`、
  `--clouddialogtest 20/20`、`--contenttest 5/5`、`--badgetest 28/28`、
  `--launchtest 11/11`。
- 权限边界：外部 `index.json` / `config.json` / `jobs\queue` 快照**差异 0 处**；
  `D:\Wallpaper` 67 个文件、280,056,115 字节不变；无残留进程；
  `jobs\buddy` 真实目录保持空（自检全部隔离在仓库内 `.selftest-tmp`）。
  仍跳过 `--deltest / --greettest / --configtest / --arrivetest`。
  本回合**没有**调用任何会消耗积分的多模态生成工具，也没有写入系统剪贴板。

### 4. 版本

`package.bat` / `docs/readme.txt` 升 **v0.9.2**，重新打包
`dist\WallpaperManager-v0.9.2.zip`。待办不变：远程 URL、`index.json` 里
新图误写的动效标记、`jobs\done\` 假工单、真 Key 链路，均按权限边界等主人授权。

### 5. 11-15 追加：「不打开 WorkBuddy 也能出片吗」+ 真出一片验收

主人再问：「以后使用我的壁纸软件，能不打开 WorkBuddy 也能图生视频吗？」
并再次强调「直接做成完整可用的功能，而不是只给我一个页面和点击之后没有任何反应的按钮」。

**结论（已写进软件，不再只停在口头）**
- 用 WorkBuddy 积分：**不能**。积分绑定 WorkBuddy 客户端会话，自动领单是客户端里的
  定时任务，独立 exe 拿不到会话凭证、也没法自己扣积分。客户端没开时待领工单会
  留着，不自动出片、不扣积分。
- 完全不打开 WorkBuddy：**能，但只能走按秒付费**。设置切成「服务商 API Key」
  → 填自己的百炼 / 方舟 Key → 程序内直连「提交 → 轮询 → 下载」，
  不依赖 WorkBuddy、不消耗积分（720P 约 ¥0.45 / 3 秒）。这条链路 v0.6 起就有。
- 没有第三条路：除非 WorkBuddy 将来开放可供外部程序调用的视频生成 API / 可导出凭证。
- 文案落点：`CloudGen.BuddyNote`（设置面板）、`StartBuddyGen` 三选弹窗、
  `docs/readme.txt` 新增「不打开 WorkBuddy 也能出片吗」小节。

**自动领单（主人明确授权后创建）**
`automation_update` 建 `60ee3d44-64fb-4f24-8811-bc1d17f92f78`，
「壁纸管家 · WorkBuddy 积分出片自动领单」，`FREQ=HOURLY;INTERVAL=1`、ACTIVE。
每小时只读扫描 `%APPDATA%\wallpaper\jobs\buddy\*.json`；无单立即结束、不调用生成工具；
有单按工单里的 image 作首帧出片、写到 out、确认落地才删工单；
严禁改 index.json / queue / done / config.json、严禁调付费 API。

**真出一片验收（消耗了 WorkBuddy 积分，主人已确认）**
- 选图 `biliart-27606684-1`（新下载的静态图，`videos\` 下原本没有任何同名或变体片）。
- 用 WorkBuddy 多模态图生视频（VideoGen，`image` = 源图首帧）生成飘动短片；
  工具最短 4 秒（3 秒是程序下单侧上限，播放端不限时长），按 4 秒生成，
  积分消耗约 40~80（授权口径 50~100 积分 / 5 秒）。
- 产物 1,992,040 字节，按约定命名写入
  `D:\Wallpaper\videos\biliart-27606684-1.mp4`（SHA256 与生成件一致）。
  **这是本回合唯一新增的工作空间外文件**，按主人授权执行；
  `index.json` / `config.json` / `jobs\queue` / `jobs\done` 一律未动。
- `--searchtest` 输出「有飘动片 **4/4**」（此前 3/3），
  内容比对仍「20 张图 × 3 段未归属视频 → 认领 3 对」且只确认
  `biliart-17714251-4` 那一对 —— 新片按规范主文件名直接命中，
  **没有把别的壁纸视频串到新图上**。程序可直接点开播放。

## 二十、11-15：切换壁纸不自动播已有动态视频（v0.9.3）

### 1. 现象与根因

主人报「库里倒数第二张图切换壁纸时不会自动播放已有动态视频」。
现场（只读）：库 20 条，倒数第二张 = `biliart-17714251-4`，
`index.json` 里 `motion=true / fx=2`，`videos\` 下**没有**它的规范主文件，
片源是 v0.9 按画面判定认领的散片
`Anime_illustration_of_a_girl_w_2026-11-12T10-02-47.mp4`（MAD=0.067 / hash=8）。
运行日志显示切换时走的是 `DoSet` 的「回到静态壁纸，已关掉自动恢复」，
即 `ApplyItem` 没进 `StartDynamic` 分支。

根因是 `LibraryStore.Load` 里一道 v0.7 之前的旧闸：
`if (it.Motion && !File.Exists(videos\<id>.mp4)) { it.Motion=false; it.Effect=0; }`
只认规范主文件，跟运行时 `RelatedVideo` 的七层发现不一致。
于是载入即把这张清成静态，点卡片只设静态壁纸；而角标用
`VideoFor → RelatedVideo.Find` 仍能查到散片，显示「✦ 飘动」——
两套口径打架，正是「有片却不自动播」的体感。
同类影响：任何靠第 7 层内容判定、或 `.bak*` / 变体命名 / 子目录 / 工单 out
提供片源的图，重启程序后动效标记都会被误清。

### 2. 改动

- `RelatedVideo.cs`：新增只读 `CanPlay(libRoot, it, imagePath)`，
  判 `videos\<id>.mp4` 或 `FindSource` 的七层发现（含内容判定），
  **只找不搬**：不 `File.Copy`、不改名、不写壁纸库；扫描异常时保守保留
  用户标记（运行时 `VideoFor` 还会再判一次，没片不会播）。
- `LibraryStore.cs`：删掉只认规范主文件的降级判断，移到
  `ThumbAbs` / `ImagePath` 等路径字段赋值之后，改用
  `RelatedVideo.CanPlay(root, it, it.ImagePath)`；「真的一片都没有」仍降级为静态。
- `Program.cs`：`ApplyItem` 的两个动态分支收口到 `MotionCapable(it)`，
  与载入、角标、菜单、自检共用同一条判定；新增 `SelfTestArgs()`，
  `RestoreDynamic` / `WatchdogTick` / `RestartPlayer` 在 `--*test` / `--probe*`
  下跳过自动恢复，自检进程不再继承真实配置里的「上次动态壁纸」。
- `SelfTestExtra.cs`：新增 `SwitchMotion` 自检（`--switchmotiontest`），
  在仓库内 `.selftest-tmp` 造三种现场：变体命名片、按画面认领的散片、
  确实无片；最后只读核对真实库倒数第二张。

### 3. 验证（全绿）

- `build.bat --noboot` 两次，`build.log` / `build-player.log` 均 0 字节。
- 新增 `--switchmotiontest 17/17 PASS`：三种现场的动效标记、
  「索引载入纯只读不复制片子」、切换分支与角标判定一致、
  散片按画面认领且指向原文件；真实库 `下标 18 / 共 20` 确认倒数第二张，
  载入后 `Motion=True fx=2`、切换会走自动开播、真实库未被写新文件。
- 回归中 `--launchtest` 曾 10/11：每个自检进程启动都会 `RestoreDynamic`
  拉起播放器并残留给下一项，是自检污染而非播放器故障；加上 `SelfTestArgs()`
  隔离闸后 `--launchtest 11/11`，全量 **12 项全 PASS**：
  `--switchmotiontest 17/17`、`--searchtest`（视频 16/16 静态 4/4 飘动片 4/4）、
  `--importexporttest 11/11`、`--proxytest 9/9`、`--videotest 7/7`、
  `--relatedvideotest 56/56`、`--orphantest 16/16`、
  `--clouddialogtest 20/20`、`--contenttest 5/5`、`--badgetest 28/28`、
  `--launchtest 11/11`、`--buddyhinttest 43/43`。
- 本轮把 `TEMP` / `TMP` 指向仓库内 `.selftest-tmp\`，日志与临时文件全部落在
  工作空间内。外部 `index.json` / `config.json` / `jobs\queue` / `jobs\buddy`
  快照**差异 0 处**；`D:\Wallpaper` 68 文件 / 282,048,155 字节不变，
  `videos\biliart-17714251-4.mp4` 仍不存在（片源保持内容判定散片）；
  跑完无残留进程。
- 编译脚本按既有流程结束了正在运行的旧实例（否则无法替换 exe）；
  旧进程跑的是旧代码，修复需重新打开壁纸管家生效。

### 4. 版本与待办

`package.bat` / `docs/readme.txt` 升 **v0.9.3**，重新打包
`dist\WallpaperManager-v0.9.3.zip`。待办不变：远程 URL、`index.json` 里
新图误写的动效标记、`jobs\done\` 假工单、真 Key 链路，均按权限边界等主人授权。

## 二十一、11-16：功耗守卫、崩溃兜底、删除进回收站（v0.9.5）

### 1. 这一轮做什么

15 号的交接文档把日常使用缺口按阻断程度分了 P0/P1/P2。本轮只做其中
**需求明确、不依赖架构选择**的三项（P1-4 / P1-5 / P1-6），
P0-1 出片提速、P0-2 delogo 内建、P1-3 多屏都需要主人先定方向，没有动。

### 2. 改动

- 新增 `app/PowerGuard.cs`：`GetSystemPowerStatus` 读真实电源状态；
  判定抽成纯函数 `Decide(acLine, percent, threshold, pauseOnBattery, out why)`，
  只有「开着守卫 + 确实电池 + 电量已知且不高于阈值」才返回 true。
  电量未知（255）、插着电、守卫关闭一律不暂停。
- 新增 `app/Recycle.cs`：`SHFileOperation` + `FOF_ALLOWUNDO` 把文件/目录送回收站。
  返回 false（网络盘 / 策略关闭）时由调用方退回硬删，删除流程不被卡住。
- `AppConfig` 加 `PauseOnBattery`（默认 true）/ `BatteryPausePercent`（默认 20）。
  `Load` 缺键回填默认值，`WriteTo` 写出。`Json.Long` 缺键返回 0，
  所以阈值只用「>0 且在 0-100 内」覆盖，关守卫请用 `pauseOnBattery=false`。
- `Program`：
  - `InstallCrashHandlers`：`DispatcherUnhandledException` 接住界面线程异常、
    只弹一次可读提示并继续运行；`AppDomain.UnhandledException` 兜后台线程。
    两者都写 `%TEMP%\wallpaper-crash-YYYYMMDD-HHmmss.log` 并进运行日志。
  - `OpenLog` + 托盘「打开日志」：不用再人工翻临时目录。
  - `SetupPowerWatch` / `CheckPower`：60 秒一次，电池低电 `Dynamic.Pause()`，
    插电 `Dynamic.Resume()`；`--*test` / `--probe*` 下不启动，避免干扰播放状态断言。
  - `skipGreeting` 从一长串显式名单改成 `SelfTestArgs()`：
    以后新加自检不用再记得补名字，不会莫名其妙弹全屏欢迎窗。
- `LibraryStore`：`DeleteUnder` 先回收站、失败再硬删；顺带删掉那条
  「功耗守卫：老配置里没有这两个键…」的注释 —— 它下面以前一行实现都没有。
- `Settings`：加「电池供电且电量低时暂停动态壁纸」复选框 + 「电量阈值（0-100）」输入框。
- `build.bat` 源码列表加 `PowerGuard Recycle`。

### 3. 顺手修掉的真实缺陷（不是我这轮引入的）

`--clouddialogtest` 在真实 `config.json` 的 `genChannel=buddy`（现在的默认值）时会卡死：
这段自检本来测「服务商 API 缺配置时的总闸」，但 `RegenCloud` 在积分通道下会走
`StartBuddyGen` 的确认框，而 `CloudConfigGuard` 只注入了 `SelfTestNoDialogs`、
没有注入 `SelfTestNoBuddyDialogs`，于是真弹窗等用户点，自检永远等不到结论。
修法：自检里把 `GenChannel` 固定为 `api`，同时注入 `SelfTestNoBuddyDialogs /
SelfTestBuddyAnswer`，`finally` 原样还原。修后 20/20。

### 4. 验证（全绿）

- `build.bat --noboot` 两次，`build.log` / `build-player.log` 均 **0 字节**。
- 新增 `--guardtest 14/14 PASS`：
  功耗判定 9 项（15% 暂停 / 等于阈值也暂停 / 21% 放过 / 插电放过 / 守卫关闭放过 /
  电量未知放过 / 电源状态未知放过 / 阈值 150 夹到 100 / 阈值 -5 夹到 0）、
  真实读电源状态（本机交流 100%，异常安全）、
  回收站单文件 + 整个子目录（原路径消失）、
  新字段落盘往返、老配置缺键回填 开 / 20%。
- 全量隔离回归 **14 项全 PASS**：
  `--searchtest` PASS、`--importexporttest 11/11`、`--proxytest 9/9`、
  `--videotest 7/7`、`--relatedvideotest 56/56`、`--orphantest 16/16`、
  `--clouddialogtest 20/20`、`--contenttest 5/5`、`--badgetest 28/28`、
  `--launchtest 11/11`、`--switchmotiontest 17/17`、`--buddyhinttest 51/51`、
  `--autogentest 43/43`、`--guardtest 14/14`。
- 设置页真渲染（`--settingstest` 截图 560×880）：功耗复选框与阈值框显示正常、
  与其它控件对齐，无重叠或截断。
- 真实启动验证：数据目录正确、动态壁纸恢复开播、无 crash 文件、进程存活。
- 权限边界：`TEMP`/`TMP` 指向仓库内 `.selftest-tmp\`；
  `D:\Wallpaper` 与 `appdata` 在今天 09:35 之后 **0 个文件改动**
  （`find -newermt` 为空）；`jobs\queue` / `jobs\buddy` 均为空。
- **如实记录的副作用**：`--guardtest` 的回收站断言按功能本义把 2 个几十字节的
  测试文件（建在 `.selftest-tmp` 下）送进了系统回收站，清空回收站即可移除。

### 5. 版本与未做

`package.bat` 升 **v0.9.5**，打包 `dist\WallpaperManager-v0.9.5.zip`。

没做、需要主人拍板的（11-16 上午已问过，结论见第 6 节）：

1. **P0-1 云端出片提速** → 主人决定**不做**（验证结论见第 6 节）。
2. **P0-2 delogo 内建成程序能力**：引用本机 WorkBuddy 自带 ffmpeg（脆弱），
   还是随包分发一份（+100MB）→ 主人选**引用本机 ffmpeg**。
3. **P1-3 多屏**：每屏一个播放器实例，还是让用户选一块屏 → 主人选**先不做**。
4. **P2 出片秒数 3 秒 vs 平台最短 4 秒**：见第 6 节的参数标定。
5. **P2 收藏功能**：`SetFav` 至今零调用 → 主人选**不做**（保持现状）。

### 6. 追加：API Key 直连通道参数标定（同日）

主人中途定调：「出片提速不做，把填入 API 通道打通，以后我再充值」。
于是把「服务商 API Key 直连」这条付费链路从头审了一遍，修掉三个
「充值后第一次就可能失败或账单对不上」的问题。

**① 通义默认按有声计费（账单翻倍）**
`wan2.6-i2v-flash` 的 `audio` 参数默认 `true`，官方价区分有声/无声
（720P 无声 ¥0.15/秒、有声 ¥0.3/秒）。程序没传 `audio`，等于按有声价扣钱，
而界面显示的是无声价。现在 wan2.6 系列显式传 `"audio":false`（壁纸不需要声音）。
证据：`--genapicheck` 落盘的请求体里有 `"audio":false`。

**② 秒数固定传 3，部分模型直接报参数错**
通义 `wan2.2-i2v-flash` / `wan2.1-i2v-plus` **固定 5 秒**，传 3 会被服务端拒。
现在 `GenModelInfo` 带 `MinSeconds` / `MaxSeconds`，新增纯函数
`GenModels.ClampSeconds(m, seconds)`，所有下单与估价路径统一夹取：

| 模型 | 合法秒数 | 依据 |
|---|---|---|
| wan2.6-i2v-flash / wan2.6-i2v | 2–15 | 官方 duration 文档 |
| wan2.2-i2v-flash | 固定 5 | 官方 duration 文档 |
| doubao-seedance-1-0-pro(-fast) | 3–12 | 官方示例 5 秒；区间按常见取值，服务端如报错以服务端为准 |

`Cost()` 也改成按夹取后的秒数算，界面价格与真实出片时长、账单三者一致。
设置页在下单前会写明「这个模型只支持 X-Y 秒，按 N 秒算」。

**③ 402 没有可读提示**
`Http.Describe` 补了 402：「余额或额度不足（402）：去服务商控制台充值后再试」。
之前只会落到 `HTTP 402 Payment Required`，主人看不出要充值。

**顺手修的第二处隔离缺陷**：`--cloudflowtest` 也假设走 `GenCloud → CloudChoose`
（API 分支），但真实 config 默认 `genChannel=buddy` 时 `GenCloud` 直接进
`StartBuddyGen`，第 0 步断言变成「取消这单」而不是「去设置」。
已在自检里固定 `GenChannel=api`、`finally` 还原。修后全链路 PASS，
日志可见「¥0.45（0.15 元/秒 × 3 秒，720P）」——正是无声价。

**P0-1 网关提速的可行性结论（只读验证，未触发任何出片）**
- 15 号记的 `127.0.0.1:50556` 已不存在；今天客户端网关在 **62952**（PID 12792），
  说明**端口是每次启动动态分配的**，硬编码必然失效。
- 带 `Authorization: Bearer <客户端进程环境变量里的口令>` 可访问：
  `/api/v1/health` 200、`/api/v1/sessions` 200、`/api/v1/status` 200。
- `/api/v1/status` 的 `activeSessionId` 就是**当前正在用的会话**（`busy:true`），
  页面端点清单也只有 `POST /api/v1/runs`（启动一次 Agent run）与 SSE 流。
  外部程序想稳定借它出片，等于占用/打断主人当前会话，不可接受。
- 结论：**放弃这条路线**（主人也已决定不做），保留工单排队 + API 直连两条通道。


## 二十二、11-16：AI 角标验收做成程序能力（v0.9.6）

主人 11-15 拍板的口径是「先生成，出片后抽帧检测，再用 delogo 对角标区域局部修补，
修补干净才交付」。但此前这一步**只写在给 WorkBuddy 会话的指令里**，程序自己不验：
会话没照做、或换了台机器，带角标的片子就直接进 `videos\` 了。
11-16 上午主人定「引用本机已有的 ffmpeg」，于是把它落成 `app/Watermark.cs`。

### 1. 角标长什么样：用真实库标定，不靠猜

从 `D:/Wallpaper/videos/_raw/biliart-17403571-4.raw.mp4`（未处理的原片）抽帧实测：
「AI生成」是**圆角矩形描边**，1344×768 下占 **x 10..91 / y 10..47（82×38）**。
比例换算后：

| 比例 | 值 | 用途 |
|---|---|---|
| x/W | 0.0074 | 框左边界 |
| y/H | 0.0130 | 框上边界 |
| w/W | 0.0612 | 框宽 |
| h/H | 0.0495 | 框高 |

再加 4px 留白（delogo 要盖住描边外侧的过渡像素）→ 1344×768 得 `x=6,y=6,w=90,h=46`，
1920×1080 得 `x=10,y=10,w=126,h=61`。早期文档里写的 `x=2,y=4,w=88,h=44` 是同一处角标，
留白给得更松；现在按比例算，换分辨率不会漏。

### 2. 判定：只认「上下两条等长长横线」这个结构特征

不比对模板、不猜颜色（角标在白背景上几乎同色，按亮度永远抓不准）。做法：
1. 只有角标框那一小块抽成灰度（ffmpeg `crop`+`format=gray`，rawvideo 直出，不解码全帧）；
2. 减去 15×15 均值当「局部背景」，差值 > 8 灰阶算笔画 → 得笔画占比 `pct`；
3. 逐行求最长连续笔画长度，门槛 `L = 0.035 × 帧宽`（1344 → 47px）；
4. **存在两行都 ≥ L，且行距落在 0.035H~0.070H 之间 → 认定角标**（圆角矩形的上下边）。

全库 24 个视频 × 3 个时间点的标定结果（`pct` / 最长横线 / 是否两行）：

| 样本 | pct | 最长横线 | 两行 | 结论 |
|---|---|---|---|---|
| `_raw/biliart-17403571-4.raw.mp4`（原片） | 19.7 | 74 | ✔ | 检出 |
| `biliart-27606684-1.mp4` | 21.2 | 74 | ✔ | 检出 |
| `Anime_illustration...T02-18-26.mp4` | 19.7 | 74 | ✔ | 检出 |
| `Anime_illustration...T10-02-47.mp4`（角标与背景几乎同色） | 12.0 | 70 | ✔ | 检出 |
| `biliart-21151635-1.mp4`（只补掉一半，剩右边框） | 5.2 | 72 | ✘（只一行） | 不误判 |
| `biliart-38588664-1.mp4`（被涂成暗块） | 6.0 | 33 | ✘ | 不误判 |
| 其余 18 个（1080P 素材 / 已清理片） | 0.0~22.6 | ≤47 | ✘ | 零误报 |

要点：单看「横线长」会误伤（`biliart-10096573-7` 有 22px 的亮边、`floating-city-dreams`
有 36px 的建筑边），必须**两行同时成立且行距对**。这条规则在 24 个样本上零误报。

### 3. 修补与复查

检出后用 `ffmpeg -vf delogo=x:y:w:h -c:v libx264 -crf 18 -preset slow -pix_fmt yuv420p -an`
重编码（实测 5 秒片约 3.7 秒，后台线程跑，不挡界面）。原片先留 `<id>.mp4.bak-wm`，
新片写到 `.wm-tmp.mp4` → **再量一次**，确认角标没了才换上去。复查不干净就删掉临时文件、
保留原片、如实报「不入库」。

三种返回值语义（调用方必须照着说，不许含糊）：
- `Checked=false` → 本机没 ffmpeg，**没验**；片子留着（花了钱不能丢），状态栏写明「未验收」；
- `Checked=true, Clean=true` → 验过且干净（或修干净了）；
- `Checked=true, Clean=false` → 有角标且没修干净，**不换上、不标动态**。

### 4. 接在哪两个地方

- **API Key 直连**：`RunApiJob` 下载完成后、交给 `SweepCloudJobs` 换上之前；
- **WorkBuddy 积分通道**：`SweepBuddyJobs` 收到货、还没标 `Motion` 之前。

只对**云端生成的片子**自动动手；主人自己导入的视频一律不碰，交给托盘
「检查水印（AI 角标）」：先扫全库列清单 → 问一句 → 才修，改的每个文件都留 `.bak-wm`。

### 5. 自检 `--watermarktest`（22/22，全隔离）

- 几何 3 条：1344×768 / 1920×1080 的框必须盖住实测角标，超小画面不越界；
- 判定 5 条（**纯函数，合成图断言**，不碰 ffmpeg）：合成角标检出、纯渐变不检出、
  只有一条横线不误判、两行行距太近不检出、横线太短不检出；
- ffmpeg 发现 2 条：能找到；手填路径无效时仍自动回落；
- 端到端 8 条：用 `lavfi color` + `drawbox` 现场造一段 1344×768 带角标的短片
  → 检出（笔画 11.2%、长线 82px）→ delogo 修好 → 复查 0.0% → `.bak-wm` 存在
  → 再跑一次幂等不重编码 → 干净片子一个字节都不动 → 文件不存在时明确报「没验收」；
- **真实样本 4 条**（命令行传一个 mp4 副本才跑）：`_raw/biliart-17403571-4.raw.mp4`
  的副本 → 检出（笔画 19.7%、长线 74px）→ 修补 → 复查 0.0% →
  2,388,693 字节 → 3,321,276 字节（CRF 18）。
  合成方框描边毕竟不是真角标（圆角、半透明、与背景同色只有真片子才有），
  所以真实样本这一节不能省。

### 6. 已知边界（写清楚，别再自欺）

- **只认左上角合规角标**。别的平台水印（右下角台标、中央大字）不在此列 ——
  它们本来也不该由程序猜着涂掉。
- **半修补残迹不重复处理**：`biliart-21151635-1`（剩右边框）、`biliart-38588664-1`
  （被涂成暗块）判定为「不是完整角标」，保持原样。主人要在「检查水印」里显式决定重出，
  不擅自消耗积分重生成。
- **没 ffmpeg 时不假装干净**：如实报「未验收」，片子保留。
- 修补是重编码，画质按 CRF 18；实测 2.39MB 原片 → 3.32MB（+39%），CRF 16 会涨到 3.99MB
  但看不出多出来的画质，没必要。角标区域用四周像素插值，浅色背景上几乎看不出，
  细节复杂的背景会有一小块平滑区 —— 这比留一个「AI生成」角标准则上更可接受。

## 二十三、11-16：残迹判定 + 多帧验收（v0.9.7）

主人一句话点出来的真问题：「目前最后一张图去水印效果很差」。
查下来**不是修补算法的问题，是检测器放过了它** —— 那张一直在桌面上播，
左上角还留着一个圆角框残影（右边框 + 下边框亮线，左边被抹成一片）。

### 1. 根因：只认「完整角标」，半修补的残迹当成干净

v0.9.6 的判定是「上下两条长横线都 ≥ 门槛 且 行距对」。
但一张片子如果**被别的工具（或文档里那个写死的 88×44 框）补过一半**，
剩下的是一堆残缺：可能只有一条长横线，可能是「长横线 + 右侧竖边」。
这种既不是完整角标，就一路被判成「不是角标 → 不用处理」，于是永远留着。

实测两张（都是在 `videos\` 里真实存在的、主人能看到的）：

| 文件 | 笔画 | 最长横线 | 最长竖线 | 残的是什么 |
|---|---|---|---|---|
| `biliart-21151635-1.mp4` | 5.2% | 72px | 27px | 圆角框右边 + 下边亮线 |
| `biliart-38588664-1.mp4` | 6.0% | 33px | 32px | 整块比背景亮的灰色斑块 |

### 2. 改法：完整角标 / 残迹，两种都修

在 `Watermark.Analyze` 里补一路判定：

```
长横线门槛 L  = 0.035 × 帧宽
长竖线门槛 Lv = 0.025 × 帧高
完整角标 = 两行 ≥ L 且行距 0.035H~0.070H   （原有）
残迹     = 不是完整角标，且笔画 ≥ 1.5%，且
           (横线 ≥ L)  或  (竖线 ≥ Lv 且 横线 ≥ L/2)
```

**只认竖线会误伤**：真实画面里竖直的建筑边、光柱也很长
（`sleepy-rainy-evening` 竖线 53px、`urban-calm-cat` 25px），
所以竖线必须配一条够长的横线才算残迹。

全库 23 个交付样本复核（三个时间点各判一次）：

- 检出 2 个：正好是上面那两张残迹；
- 其余 21 个全部干净，**零误报**；
- 库里最长横线是 `floating-city-dreams` 的 36px（门槛 67px），
  最长竖线是 `sleepy-rainy-evening` 的 53px（但它横线只有 24px，配不上）——
  都离阈值有余量。

### 3. 复查也必须多帧，抽帧也要多帧

- **验收从 1 帧改成 3 帧**（1.0 / 0.4 / 2.2 秒）：只抽一帧的话，万一正好抽到闪光、
  转场、镜头发白的那一帧，整套判定就漏了，片子带角标进库。
  26 个样本 × 3 帧判定完全一致，不引入误报。
- **修补后的复查同样跑 3 帧**：任一时间点还有痕迹就判「不干净、不入库」。
  以前只查 1 帧，可能「1 秒干净、3 秒又露出来」还照样换上。

### 4. 备份不再被覆盖（这是真丢数据的地方）

旧代码 `if (File.Exists(bak)) File.Delete(bak);` —— 一张片子修第二次时，
会把它唯一的那份原始素材删掉，换成「本次的输入（已经修过一次的版本）」。
现在改成：`.bak-wm` 已存在就依次用 `.bak-wm2`、`.bak-wm3`…，**绝不动已有的备份**。
11-16 上午修 `biliart-27606684-1` 时就踩到过这条（当时那张只修过一次，没造成损失）。

### 5. 播放中的片子会被锁住，修补前先放掉

播放器一直开着 mp4，`File.Move` 会 sharing violation 直接失败。
托盘「检查水印」和新的 `--fixlogo` 在动手前都会先
`StopPlayback + MarkStopped`（记住被停掉的 id），全部修完后 `ApplyItem` 恢复播放。

### 6. 新增 `--fixlogo`：命令行批处理

```
WallpaperManager.exe --fixlogo          扫 videos\，把检出角标/残迹的逐个修掉
WallpaperManager.exe --fixlogo --dry    只列清单，不改任何文件
```

和托盘「检查水印」共用同一个 `Watermark.VerifyAndRepair`，区别只是不问话；
修完自动退出，方便脚本串联。已显式加进 `SelfTestArgs()` 闸门
（否则它一启动就会 RestoreDynamic 把要改的那张拉起来锁住）。

### 7. 实际修复结果（主人 11-16 11:15 授权后执行）

```
--fixlogo 开始：D:/Wallpaper/videos（24 个文件）
· biliart-21151635-1.mp4　检出 AI 残迹（笔画 5.2%，长线 72px，竖线 27px）
· biliart-38588664-1.mp4　检出 AI 残迹（笔画 6.0%，长线 33px，竖线 32px）
--fixlogo 结束：扫描 24 / 检出 2 / 修好 2 / 没修干净 0 / 读不出 0
```

| 文件 | 修补前 | 修补后 | 备份 |
|---|---|---|---|
| `biliart-21151635-1.mp4` | 1,397,293 | 1,400,748 | `.bak-wm`（11-15 原件） |
| `biliart-38588664-1.mp4` | 4,030,608 | 3,587,412 | `.bak-wm`（11-15 原件） |

放大 6 倍逐像素看：圆角框残影、灰色斑块都没了，接缝处看不出补过。
其余 22 个文件一个字节没动。

### 8. 自检 `--watermarktest` 28/28

新增 / 改写的断言：
- 只剩一条长横线 → **检出为残迹**（以前是「不检出」）；
- 行距太近但横线够长 → 不算完整角标，但仍按残迹修；
- 小方框（横线 28px + 竖边 39px）→ 不算完整角标，按残迹修；
- 长横线 + 长竖线（右边框残留）→ 检出为残迹；
- **只有一条长竖线（建筑边/光柱）→ 不误判成残迹**；
- 真实样本改成传残迹副本（两张分别跑过），走完整生产代码：检出 → 修补 → 三帧复查干净。

