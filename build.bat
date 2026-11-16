@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

rem ============================================================
rem  Zero-download build: uses the C# compiler that ships with
rem  Windows (.NET Framework), no Rust / no Visual Studio / no NuGet.
rem  Output: app\bin\WallpaperManager.exe
rem
rem  NOTE: csc writes to a temporary .new.exe first, then we stop the
rem  running instance and swap it in. This avoids error CS0016
rem  ("cannot write output file, being used by another process")
rem  when the app is still running from a previous build.
rem ============================================================

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" goto :nowpf
if not exist "%FW%\WPF\PresentationFramework.dll" goto :nowpf

if not exist "app\bin" mkdir "app\bin"

set "SRC="
for %%f in (Program Models Json Desk WallpaperFit LibraryStore Greeting TagNames Online Bili Translate Log Settings Dynamic CloudGen BuddyBridge RelatedVideo VideoFingerprint MfVideo GenApi GenModels MockCloud MockProxy SelfTestExtra Http Pinyin ColorTag WelcomeGen MainWindow PowerGuard Recycle Watermark) do set "SRC=!SRC! app\%%f.cs"

set "NEWEXE=app\bin\WallpaperManager.new.exe"
if exist "%NEWEXE%" del /f /q "%NEWEXE%" >nul 2>&1

rem icon: use it if present, never block the build
set "ICONOPT="
if exist "resources\app.ico" set ICONOPT=/win32icon:"resources\app.ico"

echo [1/4] Compiling WallpaperManager.exe
"%FW%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /unsafe+ /warn:1 /codepage:65001 /out:"%NEWEXE%" !ICONOPT! ^
 /r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" /r:"%FW%\System.Drawing.dll" ^
 /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Web.Extensions.dll" /r:"%FW%\System.Xml.dll" /r:"%FW%\System.Xaml.dll" ^
 /r:"%FW%\WPF\WindowsBase.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\PresentationFramework.dll" ^
 !SRC! > "build.log" 2>&1

if errorlevel 1 goto :fail
if not exist "%NEWEXE%" goto :fail

rem player is built separately: it injects into the desktop layer and will
rem later host D3D/MediaFoundation; a crash there must not kill the manager.
set "PLAYERNEW=app\bin\WallpaperPlayer.new.exe"
if exist "%PLAYERNEW%" del /f /q "%PLAYERNEW%" >nul 2>&1

echo [2/4] Compiling WallpaperPlayer.exe
set "NOVIDEO="
set "PLAYERCOMMON=/nologo /target:winexe /platform:anycpu /optimize+ /unsafe+ /warn:1 /codepage:65001 /out:"%PLAYERNEW%" !ICONOPT! /r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" /r:"%FW%\System.Drawing.dll" /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Web.Extensions.dll""

rem video mode decodes with Media Foundation (mfreadwrite.dll) and paints with GDI.
rem it is optional: if it fails to compile, fall back to a probe-only build.
"%FW%\csc.exe" !PLAYERCOMMON! ^
 app\WallpaperPlayer.cs app\Log.cs app\Probe2.cs app\MfVideo.cs app\PlayerVideo.cs app\WallpaperFit.cs > "build-player.log" 2>&1

if errorlevel 1 (
    echo       video mode did not compile, retrying probe-only build
    copy /y "build-player.log" "build-player-video.log" >nul 2>&1
    "%FW%\csc.exe" !PLAYERCOMMON! app\WallpaperPlayer.cs app\Log.cs app\Probe2.cs > "build-player.log" 2>&1
    if errorlevel 1 goto :failplayer
    set "NOVIDEO=1"
)

if not exist "%PLAYERNEW%" goto :failplayer

echo [3/4] Stopping running instance
rem manager FIRST: if the player dies while the manager still lives, the
rem watchdog instantly respawns it and we end up unable to replace the exe.
taskkill /f /im WallpaperManager.exe >nul 2>&1
taskkill /f /im WallpaperPlayer.exe >nul 2>&1
if not errorlevel 1 (
    echo       previous instance terminated, waiting for file handle...
    ping -n 2 127.0.0.1 >nul 2>&1
) else (
    echo       no running instance
)

echo [4/4] Installing and starting
set "TRY=0"
:swap
move /y "%NEWEXE%" "app\bin\WallpaperManager.exe" >nul 2>&1
if errorlevel 1 (
    set /a TRY+=1
    if !TRY! GEQ 6 goto :failbusy
    ping -n 2 127.0.0.1 >nul 2>&1
    goto :swap
)

rem the player exe must be swapped too, otherwise the manager keeps launching a
rem STALE player (that is how a "fix" can look like it did nothing).
set "PTRY=0"
:pswap
move /y "%PLAYERNEW%" "app\bin\WallpaperPlayer.exe" >nul 2>&1
if errorlevel 1 (
    set /a PTRY+=1
    if !PTRY! GEQ 6 goto :failplayerbusy
    taskkill /f /im WallpaperPlayer.exe >nul 2>&1
    ping -n 2 127.0.0.1 >nul 2>&1
    goto :pswap
)

if not exist "app\bin\resources" mkdir "app\bin\resources"
copy /y "resources\greetings.json" "app\bin\resources\greetings.json" >nul
copy /y "resources\titles-zh.json" "app\bin\resources\titles-zh.json" >nul
if exist "resources\app.ico" copy /y "resources\app.ico" "app\bin\resources\app.ico" >nul

echo.
echo Build OK --^> app\bin\WallpaperManager.exe
if defined NOVIDEO echo NOTE: video mode was NOT compiled in (see build-player-video.log); probe still works
rem --noboot: compile/exchange only, do not launch the GUI (used by CI / self-tests).
if /i not "%~1"=="--noboot" echo Starting (auto-play first video to verify dynamic wallpaper)...
if /i not "%~1"=="--noboot" start "" "app\bin\WallpaperManager.exe" --dynvideo
exit /b 0

:failplayerbusy
echo.
echo ============== CANNOT REPLACE PLAYER ==============
echo WallpaperPlayer.exe is still locked by a running process.
echo Run:  taskkill /f /im WallpaperPlayer.exe   then build again.
echo ===================================================
pause
exit /b 1

:failplayer
echo.
echo ============ PLAYER BUILD FAILED ============
type "build-player.log"
echo =============================================
echo Manager built OK, player did not compile.
echo The old player (if any) is still usable.
pause
exit /b 1

:fail
echo.
echo ================= BUILD FAILED =================
type "build.log"
echo ===============================================
echo Full log saved to build.log
echo Running exe (if any) was NOT touched, the old build is still usable.
pause
exit /b 1

:failbusy
echo.
echo ============== CANNOT REPLACE EXE ==============
echo Compile succeeded, but the previous WallpaperManager.exe is still locked.
echo It is probably running with administrator rights, or held by antivirus.
echo Close the app from the tray icon (right click - exit), then run this again.
echo The new build is kept at %NEWEXE%
pause
exit /b 1

:nowpf
echo.
echo .NET Framework 4.x was not found in:
echo   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319
echo This Windows install looks unusual. Cannot continue.
pause
exit /b 1
