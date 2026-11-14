@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0.."

rem Build a throwaway player exe and run --fxdump on one image (offline, no desktop mount).
rem usage: build-fxdump.bat "<image>" "<outdir>" [W H]

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"

set "EXE=%TEMP%\wp-fx.exe"
if exist "%EXE%" del /f /q "%EXE%" >nul 2>&1

"%FW%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /unsafe+ /warn:1 /codepage:65001 /out:"%EXE%" ^
 /r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" /r:"%FW%\System.Drawing.dll" ^
 /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Web.Extensions.dll" ^
 app\WallpaperPlayer.cs app\Log.cs app\Probe2.cs app\MfVideo.cs app\PlayerVideo.cs app\PlayerImage.cs app\PlayerEffect.cs app\WallpaperFit.cs > "build-fxdump.log" 2>&1
if errorlevel 1 goto :bad

"%EXE%" --fxdump "%~1" "%~2"
exit /b 0

:bad
type "build-fxdump.log"
exit /b 1
