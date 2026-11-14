@echo off
setlocal
cd /d "%~dp0.."
set "EXE=%TEMP%\wp-fx.exe"
if exist "%~dp0..\fxvid" rmdir /s /q "%~dp0..\fxvid" >nul 2>&1
mkdir "%~dp0..\fxvid" >nul 2>&1
"%EXE%" --fxvideo "D:/Wallpaper/images/coastal-cliffs-storm.jpg" "%~dp0..\fxvid\sea" 8 24
"%EXE%" --fxvideo "D:/Wallpaper/images/synthwave-bike-ride.jpg" "%~dp0..\fxvid\synth" 8 24
"%EXE%" --fxvideo "D:/Wallpaper/images/biliart-17403571-4.jpg" "%~dp0..\fxvid\portrait" 8 24
exit /b 0
