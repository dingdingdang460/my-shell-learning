@echo off
setlocal
cd /d "%~dp0"

rem ============================================================
rem  Package: turn app\bin into a portable folder + zip
rem  Output: dist\WallpaperManager-v0.9.7\ and .zip
rem  No admin rights, no registry writes.
rem
rem  11-13: paid channel goes straight to the model API (Wanx / Ark)
rem  from inside the app. The local motion script is RETIRED, so
rem  tools\gen-motion.* is no longer shipped. Target machines need
rem  neither Python nor numpy / Pillow.
rem  11-15: DEFAULT channel is WorkBuddy credits (no API Key needed);
rem  switch to "Service API Key" in Settings only for paid direct generation.
rem ============================================================

set "VER=v0.9.8"
set "DST=dist\WallpaperManager-%VER%"

if not exist "app\bin\WallpaperManager.exe" goto :nobuild
if not exist "app\bin\WallpaperPlayer.exe" echo NOTE: WallpaperPlayer.exe missing, dynamic wallpaper disabled

if exist "dist" rmdir /s /q "dist"
mkdir "%DST%\resources"

echo [1/3] copy exe
copy /y "app\bin\WallpaperManager.exe" "%DST%\" >nul
if exist "app\bin\WallpaperPlayer.exe" copy /y "app\bin\WallpaperPlayer.exe" "%DST%\" >nul

echo [2/3] copy resources
copy /y "resources\greetings.json" "%DST%\resources\" >nul
copy /y "resources\titles-zh.json" "%DST%\resources\" >nul
if exist "resources\app.ico" copy /y "resources\app.ico" "%DST%\resources\" >nul
if exist "docs\readme.txt" copy /y "docs\readme.txt" "%DST%\readme.txt" >nul

echo [3/3] zip
powershell -NoProfile -Command "Compress-Archive -Path '%DST%\*' -DestinationPath '%DST%.zip' -Force"

echo.
echo Done:
echo   %DST%\       (portable folder)
echo   %DST%.zip
echo   NOTE: default cloud channel uses WorkBuddy credits (no API Key needed).
echo         Switch to "Service API Key" in Settings only for paid per-second
echo         direct generation. No Python or local script is required.
exit /b 0

:nobuild
echo.
echo No build output yet. Run build.bat first.
pause
exit /b 1
