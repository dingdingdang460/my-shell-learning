@echo off
setlocal EnableDelayedExpansion
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

echo STEP 1 - GET A TOKEN (one time, about 1 min):
echo   Open: https://github.com/settings/tokens/new
echo   Note: wallpaper-backup; Expiration: 90 days
echo   Tick scope "repo" (for this PUBLIC repo, "public_repo" also works).
echo   Generate, then copy the token.
echo.
echo STEP 2 - PASTE IT:
echo   Notepad will open with pat.txt. Paste the token as the ONLY content,
echo   save and close Notepad, then come back here and press any key.
echo.

if not exist "%PATFILE%" (
    > "%PATFILE%" echo PASTE_YOUR_GITHUB_TOKEN_HERE
)
if not exist "%PATFILE%" goto :createfail

start "" notepad "%PATFILE%"
echo.
set /p "DUMMY=After saving pat.txt in Notepad, press Enter here to continue... "

rem Read first non-empty line, strip surrounding quotes and spaces.
set "PAT="
for /f "usebackq delims=" %%L in ("%PATFILE%") do (
    if not defined PAT (
        set "LINE=%%L"
        set "LINE=!LINE:"=!"
        set "PAT=!LINE: =!"
    )
)
if not defined PAT goto :nopat
if /i "%PAT%"=="PASTE_YOUR_GITHUB_TOKEN_HERE" goto :nopat

echo.
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

:createfail
echo Cannot create pat.txt here. Check write permission for this folder.
set "PUSH_RC=1"
goto :done

:nopat
echo pat.txt is empty or still the placeholder. Aborted. Nothing was pushed.
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
