# =====================================================================
#  gen-motion.ps1 —— 壁纸动效「出片」脚本（本地生成，不花云端额度）
#
#  它干什么：
#    读应用写下的工单（jobs\queue\<id>.json），把工单里那张静态图
#    做成一镜到底、首尾严丝合缝的 3 秒短视频，按工单约定的路径
#    落到 videos\<id>.mp4。应用每 3 秒扫一次队列，看到片子就入库、
#    标成「飘动视频」，正在播的话直接换上。
#
#  为什么是本地做：
#    真·图生视频要服务商额度；本地引擎 gen-motion.py 按画面内容分区做
#    局部动效（云飘 / 水涌 / 雨丝 / 灯光 / 中部呼吸），位移是逐像素的
#    二维形变场，不是整张图平移。零成本、不联网、首尾同帧可无缝循环。
#    想要人物真的动起来，再填 API Key 走服务商那条线。
#
#  出片档位：默认长边 1600（约 30 秒/片），加 -MaxEdge 1920 走原尺寸档。
#    找不到带 numpy/PIL 的 Python 时才退回下面的 ffmpeg 平移版（效果弱）。
#
#  怎么用：
#    设置 → 云端生成 → 外部生成器，填：
#        <本文件所在目录>\gen-motion.bat
#    之后右键「云端生成」会自动拉起，不用管它。
#
#    手动跑：
#        gen-motion.bat                     处理队列里所有工单
#        gen-motion.bat -Job <工单.json>     只处理这一单
#        gen-motion.bat -Image x.jpg -Out y.mp4   拿任意图试片
#        gen-motion.bat -SelfTest           拿库里第一张图试片
#        gen-motion.bat -DryRun -All        只打印命令，不真跑
#
#  依赖：ffmpeg.exe（脚本会在 PATH、本目录、Python 的 imageio_ffmpeg
#        包里自动找；也可以用环境变量 WALLPAPER_FFMPEG 指定）
# =====================================================================
param(
    [string]$Job = "",
    [switch]$All,
    [string]$Image = "",
    [string]$Out = "",
    [int]$Seconds = 3,
    [int]$Fps = 30,
    [double]$Strength = 1.0,
    [int]$MaxEdge = 1600,
    [switch]$DryRun,
    [switch]$SelfTest
)

$ErrorActionPreference = "Continue"

# 引擎日志是 UTF-8 中文，控制台默认按本地代码页解码会变成乱码。
# 这两行把「读」和「写」都按 UTF-8 对齐，日志里才看得懂。
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
$env:PYTHONIOENCODING = "utf-8"

# ----------------------------------------------------------------- 日志
$script:LogPath = Join-Path $env:APPDATA "wallpaper\gen-motion.log"

function Write-Log {
    param([string]$Msg, [string]$Level = "info")

    $line = "{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $Msg
    try {
        $dir = Split-Path -Parent $script:LogPath
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        # 超过 2MB 就滚一份，别让这个文件长到没法看
        if ((Test-Path -LiteralPath $script:LogPath) -and
            ((Get-Item -LiteralPath $script:LogPath).Length -gt 2MB)) {
            Move-Item -LiteralPath $script:LogPath -Destination ($script:LogPath + ".1") -Force
        }
        $enc = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::AppendAllText($script:LogPath, $line + "`r`n", $enc)
    } catch { }
    if ($Level -eq "error") { Write-Host $line -ForegroundColor Red }
    elseif ($Level -eq "warn") { Write-Host $line -ForegroundColor Yellow }
    else { Write-Host $line }
}

# ----------------------------------------------------------------- ffmpeg
function Find-FFmpeg {
    $cands = New-Object System.Collections.ArrayList

    if ($env:WALLPAPER_FFMPEG) { [void]$cands.Add($env:WALLPAPER_FFMPEG) }
    [void]$cands.Add((Join-Path $PSScriptRoot "ffmpeg.exe"))
    [void]$cands.Add((Join-Path (Split-Path -Parent $PSScriptRoot) "tools\ffmpeg.exe"))

    $cmd = Get-Command "ffmpeg.exe" -ErrorAction SilentlyContinue
    if ($cmd) { [void]$cands.Add($cmd.Source) }

    # Python 的 imageio-ffmpeg 包里带了一个静态 ffmpeg，很多机器上都有
    $globs = @(
        "$env:LOCALAPPDATA\Programs\Python\Python3*\Lib\site-packages\imageio_ffmpeg\binaries\ffmpeg*.exe",
        "$env:USERPROFILE\.workbuddy\binaries\python\versions\*\Lib\site-packages\imageio_ffmpeg\binaries\ffmpeg*.exe",
        "$env:LOCALAPPDATA\Programs\Python\Python3*\Lib\site-packages\imageio_ffmpeg\binaries\ffmpeg*.exe",
        "$env:ProgramFiles\ffmpeg\bin\ffmpeg.exe",
        "C:\ffmpeg\bin\ffmpeg.exe"
    )
    foreach ($g in $globs) {
        $hit = Get-ChildItem -Path $g -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { [void]$cands.Add($hit.FullName) }
    }

    foreach ($p in $cands) {
        if ($p -and (Test-Path -LiteralPath $p -PathType Leaf)) { return $p }
    }
    return $null
}

# ----------------------------------------------------------------- 探测
function Get-ImageSize {
    param([string]$FF, [string]$Path)

    $txt = (& $FF -hide_banner -i $Path 2>&1 | Out-String)
    if ($txt -match '(\d{2,5})x(\d{2,5})') {
        return @([int]$Matches[1], [int]$Matches[2])
    }
    return $null
}

function Test-Video {
    param([string]$FF, [string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -lt 20KB) { return $false }
    $txt = (& $FF -hide_banner -i $Path 2>&1 | Out-String)
    if ($txt -notmatch 'Duration: (\d+):(\d+):(\d+\.\d+)') { return $false }
    if ($txt -notmatch 'Video:') { return $false }
    return $true
}

# ----------------------------------------------------------------- 滤镜
#  画布分三层，每层都有用处：
#    第一层 cover 到「输出尺寸 + 四周留白 2M」，留白是给漂移用的，
#    没有它一动就露黑边；
#    第二层整体放大 2 倍再裁回 2W×2H —— 这一步是关键：
#    crop 只能按整像素移动，1080p 下慢速漂移会「顿 3 帧、跳 1 像素」，
#    在双倍尺寸里做位移，再缩回原来的大小，抗锯齿会把这一像素抹平成
#    亚像素位移，肉眼看上去就是匀速的；
#    第三层（可选）eq 的亮度呼吸，幅度只有 ±1.5/255，几乎看不出来，
#    但会让画面像「在呼吸」，不是一张死图在平移。
function Build-Filter {
    param([int]$W, [int]$H, [int]$N, [int]$M, [int]$A, [double]$Bright)

    $W2 = 2 * ($W + 2 * $M)
    $H2 = 2 * ($H + 2 * $M)
    $CW = 2 * $W
    $CH = 2 * $H
    $M2 = 2 * $M
    $A2 = 2 * $A
    $NF = $N - 1
    if ($NF -lt 2) { $NF = 2 }

    $phase = "2*PI*n/$NF"
    $eqB = [string]::Format([System.Globalization.CultureInfo]::InvariantCulture, "{0:0.####}", $Bright)

    $f = "scale=${W2}:${H2}:force_original_aspect_ratio=increase:flags=lanczos,crop=${W2}:${H2}"
    $f += ",crop=${CW}:${CH}:x='$M2+$A2*sin($phase)':y='$M2+$A2*cos($phase)'"
    # 最后一步顺手把色域从 JPEG 的 full range 收到视频的 tv range：
    # 不收的话文件会被标成 yuvj420p，个别播放器按 limited 解释就整体发灰。
    $f += ",scale=${W}:${H}:flags=lanczos:out_range=tv"
    if ($Bright -ne 0) {
        $f += ",eq=brightness='$eqB*sin($phase)':eval=frame"
    }
    $f += ",format=yuv420p"
    return $f
}

# ----------------------------------------------------------------- 出片
function Find-Python {
    # 找一个能 import numpy 和 PIL 的 python.exe
    $cands = New-Object System.Collections.ArrayList
    $cmd = Get-Command "python.exe" -ErrorAction SilentlyContinue
    if ($cmd) { [void]$cands.Add($cmd.Source) }

    $globs = @(
        "$env:LOCALAPPDATA\Programs\Python\Python3*\python.exe",
        "$env:USERPROFILE\.workbuddy\binaries\python\versions\*\python.exe",
        "$env:ProgramFiles\Python3*\python.exe"
    )
    foreach ($g in $globs) {
        Get-ChildItem -Path $g -ErrorAction SilentlyContinue | ForEach-Object { [void]$cands.Add($_.FullName) }
    }

    foreach ($p in $cands) {
        if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { continue }
        & $p -c "import numpy, PIL" 2>$null
        if ($LASTEXITCODE -eq 0) { return $p }
    }
    return $null
}

function Invoke-PythonEngine {
    param([string]$Py, [string]$FF, [string]$ImagePath, [string]$OutPath,
          [int]$Seconds, [int]$Fps, [int]$MaxEdge)

    $script = Join-Path $PSScriptRoot "gen-motion.py"
    if (-not (Test-Path -LiteralPath $script)) { return @{ Ok = $false; Msg = "缺少 gen-motion.py" } }

    if (Test-Path -LiteralPath $OutPath) { Remove-Item -LiteralPath $OutPath -Force -ErrorAction SilentlyContinue }

    $t0 = Get-Date
    Write-Log "引擎参数：长边=$MaxEdge 时长=${Seconds}s 帧率=$Fps（要原尺寸画质加 -MaxEdge 1920）"
    $out = & $Py $script --image $ImagePath --out $OutPath --seconds "$Seconds" --fps "$Fps" --max-edge "$MaxEdge" --ffmpeg $FF 2>&1
    $ms = [int]((Get-Date) - $t0).TotalMilliseconds

    foreach ($line in $out) { if ("$line".Trim().Length -gt 0) { Write-Log "引擎：$line" } }

    if (-not (Test-Video -FF $FF -Path $OutPath)) {
        return @{ Ok = $false; Msg = "引擎没产出可用视频（$ms ms）" }
    }
    $kb = [int]((Get-Item -LiteralPath $OutPath).Length / 1KB)
    return @{ Ok = $true; Msg = "分区动效 ${Seconds}s ${ms}ms ${kb}KB"; Path = $OutPath }
}

function New-Motion {
    param(
        [string]$FF,
        [string]$ImagePath,
        [string]$OutPath,
        [int]$Seconds,
        [int]$Fps,
        [double]$Strength,
        [int]$MaxEdge
    )

    if (-not (Test-Path -LiteralPath $ImagePath -PathType Leaf)) {
        return @{ Ok = $false; Msg = "找不到图片：$ImagePath" }
    }

    $size = Get-ImageSize -FF $FF -Path $ImagePath
    if (-not $size) { return @{ Ok = $false; Msg = "读不出图片尺寸（不是图片，或者文件坏了）：$ImagePath" } }

    $iw = [int]$size[0]
    $ih = [int]$size[1]

    # 长边压到 MaxEdge 以内：再大播放器也要缩，白耗解码；小图不放大，放大只会更糊。
    # Python 引擎与这里的 ffmpeg 兜底必须用同一个上限，否则两条路出的片尺寸不一致。
    $long = [Math]::Max($iw, $ih)
    $k = 1.0
    if ($long -gt $MaxEdge) { $k = [double]$MaxEdge / [double]$long }
    $W = [int][Math]::Round($iw * $k)
    $H = [int][Math]::Round($ih * $k)
    if ($W % 2 -ne 0) { $W = $W - 1 }
    if ($H % 2 -ne 0) { $H = $H - 1 }
    if ($W -lt 64 -or $H -lt 64) { return @{ Ok = $false; Msg = "图片太小（${W}x${H}），做不了壁纸视频" } }

    # ---- 首选：Python 动效引擎 ----
    # 它按画面内容分区做局部运动（云在飘、水在涌、雨在下、灯光在呼吸），
    # 和「整张图一起位移」是两回事。找不到 Python 才退回下面的 ffmpeg 平移版。
    if (-not $DryRun) {
        $py = Find-Python
        if ($py) {
            $r = Invoke-PythonEngine -Py $py -FF $FF -ImagePath $ImagePath -OutPath $OutPath -Seconds $Seconds -Fps $Fps -MaxEdge $MaxEdge
            if ($r.Ok) { return $r }
            Write-Log "动效引擎没成功（$($r.Msg)），退回 ffmpeg 平移版" "warn"
        } else {
            Write-Log "没找到带 numpy/PIL 的 Python，这次只能用 ffmpeg 平移版（效果弱一些）" "warn"
        }
    }

    $N = $Seconds * $Fps
    if ($N -lt 6) { $N = 6 }

    $short = [Math]::Min($W, $H)
    $M = [int][Math]::Max(8, [Math]::Round($short * 0.02))
    $A = [int][Math]::Max(6, [Math]::Round($short * 0.017 * $Strength))
    if ($A -gt ($M - 2)) { $A = $M - 2 }

    $vf = Build-Filter -W $W -H $H -N $N -M $M -A $A -Bright 0.006

    $dir = Split-Path -Parent $OutPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    # 先写临时文件：万一中途失败，绝不能留半个 mp4 在 videos 里 ——
    # 应用只要看到文件存在就当「到货」，半个文件会被当成好片子换上。
    $tmp = $OutPath + ".part.mp4"
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }

    $ffArgs = @(
        "-y", "-hide_banner", "-loglevel", "error",
        "-loop", "1", "-framerate", "$Fps", "-i", $ImagePath,
        "-vf", $vf,
        "-frames:v", "$N",
        "-an",
        "-c:v", "libx264", "-crf", "18", "-preset", "medium", "-profile:v", "high",
        "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-r", "$Fps",
        $tmp
    )

    if ($DryRun) {
        Write-Host ""
        Write-Host ("ffmpeg " + ($ffArgs -join " "))
        Write-Host ""
        return @{ Ok = $true; Msg = "dry-run"; Path = $OutPath }
    }

    $t0 = Get-Date
    $log = (& $FF @ffArgs 2>&1 | Out-String)
    $ms = [int]((Get-Date) - $t0).TotalMilliseconds

    if (-not (Test-Video -FF $FF -Path $tmp)) {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
        $first = ($log -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 3) -join " / "
        return @{ Ok = $false; Msg = "ffmpeg 没出片（$ms ms）：$first" }
    }

    try {
        Move-Item -LiteralPath $tmp -Destination $OutPath -Force
    } catch {
        return @{ Ok = $false; Msg = "片子出来了但写不进目标位置：$($_.Exception.Message)" }
    }

    $kb = [int]((Get-Item -LiteralPath $OutPath).Length / 1KB)
    return @{ Ok = $true; Msg = "${W}x${H} ${N}帧 ${ms}ms ${kb}KB"; Path = $OutPath }
}

# ----------------------------------------------------------------- 工单
function Complete-Job {
    param([string]$FF, [string]$JobPath, [double]$Strength, [int]$MaxEdge)

    if (-not (Test-Path -LiteralPath $JobPath -PathType Leaf)) {
        Write-Log "工单不存在：$JobPath" "warn"
        return $false
    }

    $raw = Get-Content -LiteralPath $JobPath -Raw -Encoding UTF8
    try { $j = $raw | ConvertFrom-Json }
    catch {
        Write-Log "工单不是合法 JSON，移到 fail：$JobPath" "error"
        Move-JobToFail -JobPath $JobPath -Reason "工单 JSON 解析失败"
        return $false
    }

    $id = [string]$j.id
    $img = [string]$j.image
    $out = [string]$j.out
    $sec = 3
    if ($j.seconds) { $sec = [int]$j.seconds }
    if ($sec -lt 1) { $sec = 1 }
    if ($sec -gt 10) { $sec = 10 }

    if (-not $id) {
        Write-Log "工单缺 id，移到 fail：$JobPath" "error"
        Move-JobToFail -JobPath $JobPath -Reason "工单缺 id 字段"
        return $false
    }
    if (-not $out) {
        $libRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $JobPath))
        $out = Join-Path $libRoot ("videos\" + $id + ".mp4")
    }

    # 同一张图可能被点两次、或者应用重启后又拉起一个实例：
    # 用锁文件挡住第二个，免得两路 ffmpeg 同时往一个路径写。
    $lock = Join-Path $env:TEMP ("wallpaper-gen-" + $id + ".lock")
    try {
        $fs = [System.IO.File]::Open($lock, [System.IO.FileMode]::CreateNew,
                                     [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        $fs.Close()
    } catch {
        Write-Log "已有实例在处理 $id，这次跳过" "warn"
        return $false
    }

    try {
        Write-Log "出片开始：$id（$sec 秒）→ $out"
        $r = New-Motion -FF $FF -ImagePath $img -OutPath $out -Seconds $sec -Fps $Fps -Strength $Strength -MaxEdge $MaxEdge
        if ($r.Ok) {
            Write-Log "出片完成：$id  $($r.Msg)（应用会在 3 秒内收货并换上）"
            return $true
        }
        Write-Log "出片失败：$id  $($r.Msg)" "error"
        Move-JobToFail -JobPath $JobPath -Reason $r.Msg
        return $false
    } finally {
        # 锁必须删掉，否则这张图以后再也生成不了
        Remove-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue
    }
}

function Move-JobToFail {
    param([string]$JobPath, [string]$Reason)

    try {
        $queueDir = Split-Path -Parent $JobPath
        $jobsDir = Split-Path -Parent $queueDir
        $failDir = Join-Path $jobsDir "fail"
        if (-not (Test-Path -LiteralPath $failDir)) { New-Item -ItemType Directory -Path $failDir -Force | Out-Null }

        $name = [System.IO.Path]::GetFileNameWithoutExtension($JobPath)
        $dst = Join-Path $failDir ([System.IO.Path]::GetFileName($JobPath))
        if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Force -ErrorAction SilentlyContinue }
        Move-Item -LiteralPath $JobPath -Destination $dst -Force

        # 顺着这个 .err.txt 能查到为什么没出片；应用只认 fail 里的 json，不读这个
        $enc = New-Object System.Text.UTF8Encoding($false)
        $text = "{0:yyyy-MM-dd HH:mm:ss}`r`n{1}`r`n" -f (Get-Date), $Reason
        [System.IO.File]::WriteAllText((Join-Path $failDir ($name + ".err.txt")), $text, $enc)
    } catch {
        Write-Log "标记失败时出错：$($_.Exception.Message)" "warn"
    }
}

# ----------------------------------------------------------------- 入口
Write-Log "---- gen-motion 启动：$($MyInvocation.Line)"

$ff = Find-FFmpeg
if (-not $ff) {
    Write-Log "找不到 ffmpeg.exe。三种办法任选：`r`n  1) 把 ffmpeg.exe 放到脚本同目录；`r`n  2) 装 ffmpeg 并加进 PATH；`r`n  3) 设环境变量 WALLPAPER_FFMPEG 指向它。" "error"
    exit 2
}
Write-Log "ffmpeg: $ff"

# 试片：拿任意一张图，输出到 tools\out 或指定路径
if ($Image) {
    if (-not $Out) {
        $Out = Join-Path $PSScriptRoot ("out\gen-test\" + [System.IO.Path]::GetFileNameWithoutExtension($Image) + ".mp4")
    }
    $r = New-Motion -FF $ff -ImagePath $Image -OutPath $Out -Seconds $Seconds -Fps $Fps -Strength $Strength -MaxEdge $MaxEdge
    if ($r.Ok) { Write-Log "试片完成：$Out  $($r.Msg)"; exit 0 }
    Write-Log "试片失败：$($r.Msg)" "error"
    exit 1
}

# 自检：从应用配置里取库路径，拿第一张图出片（不动任何工单）
if ($SelfTest) {
    $cfgPath = Join-Path $env:APPDATA "wallpaper\config.json"
    $lib = "D:\Wallpaper"
    if (Test-Path -LiteralPath $cfgPath) {
        try {
            $cfg = (Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8) | ConvertFrom-Json
            if ($cfg.libRoot) { $lib = [string]$cfg.libRoot }
        } catch { }
    }
    $first = Get-ChildItem -LiteralPath (Join-Path $lib "images") -Filter *.jpg -ErrorAction SilentlyContinue |
             Sort-Object Length -Descending | Select-Object -First 1
    if (-not $first) {
        Write-Log "自检失败：$lib\images 里没有 jpg，先下载几张壁纸" "error"
        exit 1
    }
    $Out = Join-Path $PSScriptRoot "out\gen-test\selftest.mp4"
    $r = New-Motion -FF $ff -ImagePath $first.FullName -OutPath $Out -Seconds $Seconds -Fps $Fps -Strength $Strength -MaxEdge $MaxEdge
    if ($r.Ok) {
        Write-Log "自检通过：$($first.Name) → $Out  $($r.Msg)"
        Write-Log "用播放器打开这个文件看一眼，确认画面在缓慢移动、循环处不跳。"
        exit 0
    }
    Write-Log "自检失败：$($r.Msg)" "error"
    exit 1
}

# 单张工单（应用拉起时走这条：genCmd 后面只跟一个工单路径）
if ($Job) {
    $ok = Complete-Job -FF $ff -JobPath $Job -Strength $Strength -MaxEdge $MaxEdge
    if ($ok) { exit 0 } else { exit 1 }
}

# 整队：不传参、或者 -All，就把队列里积压的全跑完
$queueDir = Join-Path $env:APPDATA "wallpaper\jobs\queue"
if (-not (Test-Path -LiteralPath $queueDir)) {
    Write-Log "队列目录还不存在（$queueDir），没有要处理的单" "warn"
    exit 0
}

$jobs = Get-ChildItem -LiteralPath $queueDir -Filter *.json -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime
if (-not $jobs -or $jobs.Count -eq 0) {
    Write-Log "队列是空的，没有要处理的单"
    exit 0
}

Write-Log "队列里有 $($jobs.Count) 张单，开始出片"
$done = 0
foreach ($jf in $jobs) {
    if (Complete-Job -FF $ff -JobPath $jf.FullName -Strength $Strength -MaxEdge $MaxEdge) { $done++ }
}
Write-Log "整队结束：成功 $done / $($jobs.Count)"
if ($done -eq $jobs.Count) { exit 0 } else { exit 1 }
