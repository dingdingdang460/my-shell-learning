@echo off
setlocal
cd /d "%~dp0"
set "LOG=%~dp0push-backup.log"
echo ============================================================
echo  Wallpaper Manager - Push source to GitHub backup
echo  Target: https://github.com/dingdingdang460/my-shell-learning
echo  Full output is logged to: %LOG%
echo ============================================================
echo.

rem ---- environment header (overwrite: latest run only) ----
echo ===== Run diag %time% ===== > "%LOG%"
echo [PWD] %CD% >> "%LOG%"
echo [git version] >> "%LOG%"
git --version >> "%LOG%" 2>&1
echo [git path] >> "%LOG%"
where git >> "%LOG%" 2>&1
echo [HOME] %HOME% >> "%LOG%"
echo [USERPROFILE] %USERPROFILE% >> "%LOG%"

echo First run opens a GitHub sign-in window:
echo   choose "Sign in with your browser" and authorize in the browser.
echo   Or use a PAT: user dingdingdang460, password = Personal Access Token with repo scope.
echo Credentials are remembered by Windows; later runs need no sign-in.
echo.
pause

rem Override HOME only, to bypass user-level ghproxy insteadOf rewrite.
rem System-level credential.helper stays active; GCM stores creds in Windows Credential Manager.
set "TMPHOME=%TEMP%\wallpaper-gitpush-home"
if not exist "%TMPHOME%" mkdir "%TMPHOME%"
set "HOME=%TMPHOME%"
echo [temp HOME] %HOME% >> "%LOG%"

echo [1/2] Pushing main (sign-in window on first run)...
git push https://github.com/dingdingdang460/my-shell-learning.git main >> "%LOG%" 2>&1
set "PUSH_RC=%errorlevel%"
echo.
echo ---------- output (also saved to log) ----------
type "%LOG%"
echo ------------------------------------------------
if not "%PUSH_RC%"=="0" goto :fail

echo [2/2] Verifying remote main...
git ls-remote https://github.com/dingdingdang460/my-shell-learning.git refs/heads/main >> "%LOG%" 2>&1
echo Local HEAD:
git rev-parse HEAD
echo.
echo Push OK. Future runs can just use git push.
pause
exit /b 0

:fail
echo.
echo Push failed (exit %PUSH_RC%). Full log: %LOG%
echo Common causes:
echo   1) sign-in cancelled, or PAT lacks repo write scope;
echo   2) no network access to github.com:443;
echo   3) two git installs use different credential helpers.
echo Send %LOG% to the assistant for diagnosis.
pause
exit /b 1
