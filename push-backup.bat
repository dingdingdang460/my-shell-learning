@echo off
setlocal
cd /d "%~dp0"
set "LOG=%~dp0push-backup.log"
set "PATFILE=%~dp0pat.txt"
echo ============================================================
echo  Wallpaper Manager - Push source to GitHub (PAT method)
echo  Target: https://github.com/dingdingdang460/my-shell-learning
echo ============================================================
echo.
echo Browser sign-in is blocked by a localhost IPv6 issue on this PC.
echo This script uses a Personal Access Token (PAT) instead - no browser needed.
echo.
echo SETUP (one time, about 1 min):
echo   1. Open: https://github.com/settings/tokens/new
echo   2. Note: wallpaper-backup; Expiration: 90 days
echo   3. Tick scope "repo" (for this PUBLIC repo, "public_repo" also works).
echo   4. Generate, copy the token.
echo   5. Paste it as the ONLY content of pat.txt in this folder, then save.
echo.
echo The script deletes pat.txt after the run (win or fail).
echo.

if not exist "%PATFILE%" goto :nopatfile
set /p "PAT=" < "%PATFILE%"
if not defined PAT goto :nopat
if "%PAT%"=="" goto :nopat

echo [1/2] Pushing main via PAT (this may take 10-40s)...
git push "https://dingdingdang460:%PAT%@github.com/dingdingdang460/my-shell-learning.git" main > "%LOG%" 2>&1
set "PUSH_RC=%errorlevel%"
echo.
echo ---------- git output (also in %LOG%) ----------
type "%LOG%"
echo ------------------------------------------------
del /q "%PATFILE%" >nul 2>&1
if not "%PUSH_RC%"=="0" goto :fail

echo [2/2] Verifying remote main...
git ls-remote "https://github.com/dingdingdang460/my-shell-learning.git" refs/heads/main
echo Local HEAD:
git rev-parse HEAD
echo.
echo SUCCESS. pat.txt has been deleted.
goto :done

:nopatfile
echo pat.txt NOT FOUND. Create it in this folder with your PAT as the only line.
set "PUSH_RC=1"
goto :done

:nopat
echo pat.txt is empty. Aborted.
del /q "%PATFILE%" >nul 2>&1
set "PUSH_RC=1"
goto :done

:fail
echo.
echo Push FAILED (exit %PUSH_RC%). Full log: %LOG%
echo Common causes: PAT lacks repo/public_repo scope, PAT expired,
echo   wrong username, or no network to github.com:443.
echo Send %LOG% to the assistant for diagnosis.

:done
echo.
pause
exit /b %PUSH_RC%
