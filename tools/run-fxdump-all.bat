@echo off
setlocal
cd /d "%~dp0.."
set "EXE=%TEMP%\wp-fx.exe"
if not exist "%EXE%" (
  echo missing %EXE%
  exit /b 1
)
set "OUT=%~dp0..\fxout\all"
if exist "%OUT%" rmdir /s /q "%OUT%" >nul 2>&1
mkdir "%OUT%" >nul 2>&1
for %%f in ("D:\Wallpaper\images\*.jpg") do (
  "%EXE%" --fxdump "%%~ff" "%OUT%\%%~nf"
)
exit /b 0
