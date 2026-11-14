@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0.."

rem Build ONLY WallpaperPlayer.exe into app\bin, and do not start anything.
rem Use it while iterating on the effect: --fxdump / --fxvideo need the player only.

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" goto :nofw
if not exist "app\bin" mkdir "app\bin"

set "ICONOPT="
if exist "resources\app.ico" set "ICONOPT=/win32icon:"resources\app.ico""

set "NEW=app\bin\WallpaperPlayer.new.exe"
if exist "%NEW%" del /f /q "%NEW%" >nul 2>&1

"%FW%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /unsafe+ /warn:1 /codepage:65001 /out:"%NEW%" !ICONOPT! ^
 /r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" /r:"%FW%\System.Drawing.dll" ^
 /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Web.Extensions.dll" ^
 app\WallpaperPlayer.cs app\Log.cs app\Probe2.cs app\MfVideo.cs app\PlayerVideo.cs app\PlayerImage.cs app\PlayerEffect.cs app\WallpaperFit.cs > "build-player.log" 2>&1

if errorlevel 1 goto :fail

taskkill /f /im WallpaperPlayer.exe >nul 2>&1
set "TRY=0"
:swap
move /y "%NEW%" "app\bin\WallpaperPlayer.exe" >nul 2>&1
if errorlevel 1 (
    set /a TRY+=1
    if !TRY! GEQ 6 goto :busy
    ping -n 2 127.0.0.1 >nul 2>&1
    goto :swap
)

echo PLAYER BUILD OK --^> app\bin\WallpaperPlayer.exe (not started)
exit /b 0

:busy
echo PLAYER EXE IS LOCKED. taskkill /f /im WallpaperPlayer.exe then retry.
exit /b 1

:fail
echo PLAYER BUILD FAILED
type "build-player.log"
exit /b 1

:nofw
echo .NET Framework 4.x compiler not found.
exit /b 1
