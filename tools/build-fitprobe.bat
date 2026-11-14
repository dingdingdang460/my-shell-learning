@echo off
setlocal
cd /d "%~dp0.."

rem end-to-end: make a portrait image, fit it, really set it as wallpaper,
rem grab the screen, then restore the previous wallpaper.

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"

"%FW%\csc.exe" /nologo /target:exe /codepage:65001 /out:"%TEMP%\fitprobe.exe" ^
 /r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" /r:"%FW%\System.Drawing.dll" /r:"%FW%\System.Windows.Forms.dll" ^
 app\Desk.cs app\Log.cs app\WallpaperFit.cs tools\fitprobe.cs
if errorlevel 1 exit /b 1

"%TEMP%\fitprobe.exe" %*
exit /b %errorlevel%
