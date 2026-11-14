@echo off
setlocal
cd /d "%~dp0.."

rem offline check for portrait-wallpaper fitting: builds test images in %TEMP%,
rem never touches the real library or the desktop wallpaper.

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"

"%FW%\csc.exe" /nologo /target:exe /codepage:65001 /out:"%TEMP%\test_fit.exe" ^
 /r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" /r:"%FW%\System.Drawing.dll" /r:"%FW%\System.Windows.Forms.dll" ^
 app\Log.cs app\WallpaperFit.cs tools\test_fit.cs
if errorlevel 1 exit /b 1

"%TEMP%\test_fit.exe"
exit /b %errorlevel%
