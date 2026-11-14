@echo off
rem 离线验证「删除壁纸」：只在临时目录里建一个假库，不碰真实壁纸。
rem 单独做成批处理是为了走和 build.bat 相同的编译通道。
setlocal
cd /d "%~dp0.."

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"

"%FW%\csc.exe" /nologo /target:exe /codepage:65001 /out:"%TEMP%\test_delete.exe" ^
 /r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" /r:"%FW%\System.Web.Extensions.dll" ^
 app\Models.cs app\Json.cs app\LibraryStore.cs tools\test_delete.cs
if errorlevel 1 exit /b 1

"%TEMP%\test_delete.exe"
exit /b %errorlevel%
