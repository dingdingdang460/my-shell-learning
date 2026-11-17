@echo off
setlocal
cd /d "%~dp0"
set "LOG=%~dp0push-backup.log"
echo ============================================================
echo  壁纸管家 - 推送源码到 GitHub 备份（诊断版）
echo  目标: https://github.com/dingdingdang460/my-shell-learning
echo  完整报错会写到: %LOG%
echo ============================================================
echo.

rem ---- 环境诊断头（覆盖写，每次跑都是最新一次）----
echo ===== 运行诊断 %date% %time% ===== > "%LOG%"
echo [当前目录] %CD% >> "%LOG%"
echo [git版本] >> "%LOG%"
git --version >> "%LOG%" 2>&1
echo [git路径] >> "%LOG%"
where git >> "%LOG%" 2>&1
echo [原HOME] %HOME% >> "%LOG%"
echo [原USERPROFILE] %USERPROFILE% >> "%LOG%"

echo 第一次运行会弹出 GitHub 登录窗口：
echo   选 Sign in with your browser，用浏览器登录并授权即可
echo   （或用 PAT：用户名 dingdingdang460，密码填 Personal Access Token，需勾 repo 权限）
echo 登录成功后自动推送，凭据由 Windows 记住，以后直接 git push 即可。
echo.
pause

rem 用临时 HOME 绕开 ~/.gitconfig 里的 ghproxy insteadOf 重写，直连 github.com。
rem 系统级 gitconfig 里的 credential.helper=manager 仍在，GCM 凭据存 Windows 凭据管理器，不会丢。
set "TMPHOME=%TEMP%\wallpaper-gitpush-home"
if not exist "%TMPHOME%" mkdir "%TMPHOME%"
rem 只改 HOME 即可绕开用户级 ghproxy 配置；不改 USERPROFILE，避免干扰 GCM 弹窗与凭据存储。
set "HOME=%TMPHOME%"
echo [临时HOME] %HOME% >> "%LOG%"

echo [1/2] 推送 main（首次会弹登录窗口）...
git push https://github.com/dingdingdang460/my-shell-learning.git main >> "%LOG%" 2>&1
set "PUSH_RC=%errorlevel%"
echo.
echo ---------- 下面是对话窗口看到的完整输出（同步存到日志）----------
type "%LOG%"
echo ------------------------------------------------------------------
if not "%PUSH_RC%"=="0" goto :fail

echo [2/2] 推送完成，核对远端 main 提交号...
git ls-remote https://github.com/dingdingdang460/my-shell-learning.git refs/heads/main >> "%LOG%" 2>&1
echo 本地 HEAD:
git rev-parse HEAD
echo.
echo 推送成功。以后可直接在仓库里 git push（凭据已记住）。
pause
exit /b 0

:fail
echo.
echo 推送失败（退出码 %PUSH_RC%）。完整报错已存到:
echo   %LOG%
echo 常见原因：
echo   1) 登录窗口被取消，或 PAT 没勾 repo 写权限；
echo   2) 本机连不上 github.com:443（公司网络/防火墙）；
echo   3) 本机有两套 git（WorkBuddy 自带 / 独立安装），凭据助手版本不同。
echo 请把 %LOG% 发给小微定位。
pause
exit /b 1
