@echo off
setlocal
cd /d "%~dp0.."
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
"%FW%\csc.exe" /nologo /target:exe /platform:anycpu /codepage:65001 /out:"app\bin\handofftest.exe" ^
 /r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" ^
 app\Dynamic.cs app\Log.cs tools\handofftest.cs
if errorlevel 1 exit /b 1
echo BUILD OK
