@echo off
setlocal
cd /d "%~dp0"
echo ============================================================
echo  壁纸管家 - 推送源码到 GitHub 备份
echo  目标: https://github.com/dingdingdang460/my-shell-learning
echo ============================================================
echo.
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
set "HOME=%TMPHOME%"
set "USERPROFILE=%TMPHOME%"

echo [1/2] 推送 main（首次会弹登录窗口）...
git push https://github.com/dingdingdang460/my-shell-learning.git main
if errorlevel 1 goto :fail

echo [2/2] 推送完成，核对远端 main 提交号...
git ls-remote https://github.com/dingdingdang460/my-shell-learning.git refs/heads/main
echo 本地 HEAD:
git rev-parse HEAD
echo.
echo 推送成功。以后可直接在仓库里 git push（凭据已记住）。
pause
exit /b 0

:fail
echo.
echo 推送失败。常见原因：
echo   1) 登录窗口被取消，或 PAT 没勾 repo 写权限；
echo   2) 本机连不上 github.com:443（公司网络/防火墙）。
echo 也可换个方式：用 GitHub Desktop 登录，或让小微换用连接器写权限后重推。
pause
exit /b 1
