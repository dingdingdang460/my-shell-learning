@echo off
rem ============================================================
rem  gen-motion.bat -- launcher for gen-motion.ps1
rem
rem  Fill this file's full path into:
rem    Settings -> Cloud generation -> External generator (genCmd)
rem
rem  The app starts it with a single job path:
rem    gen-motion.bat "C:\...\jobs\queue\<id>.json"
rem
rem  Double-click without arguments = process every queued job.
rem ============================================================
setlocal

set "PS=%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" set "PS=powershell.exe"

"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0gen-motion.ps1" %*
exit /b %errorlevel%
