@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0.."

rem syntax check only: compiles to %TEMP%, never replaces the exe

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"

set "SRC="
for %%f in (Program Models Json Desk WallpaperFit LibraryStore Greeting TagNames Online Bili Translate Log Settings Dynamic CloudGen MainWindow) do set "SRC=!SRC! app\%%f.cs"

echo [manager]
"%FW%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /unsafe+ /warn:1 /codepage:65001 /out:"%TEMP%\wm-check.exe" ^
 /r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" /r:"%FW%\System.Drawing.dll" ^
 /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Web.Extensions.dll" /r:"%FW%\System.Xml.dll" /r:"%FW%\System.Xaml.dll" ^
 /r:"%FW%\WPF\WindowsBase.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\PresentationFramework.dll" ^
 !SRC!
if errorlevel 1 goto :bad

echo [player]
"%FW%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /unsafe+ /warn:1 /codepage:65001 /out:"%TEMP%\wp-check.exe" ^
 /r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" /r:"%FW%\System.Drawing.dll" ^
 /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Web.Extensions.dll" ^
 app\WallpaperPlayer.cs app\Log.cs app\Probe2.cs app\MfVideo.cs app\PlayerVideo.cs app\PlayerImage.cs app\PlayerEffect.cs app\WallpaperFit.cs
if errorlevel 1 goto :bad

echo COMPILE OK
exit /b 0

:bad
echo COMPILE FAILED
exit /b 1
