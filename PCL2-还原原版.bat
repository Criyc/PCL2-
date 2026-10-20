@echo off
rem Distribution watermark: Cricy
setlocal DisableDelayedExpansion
chcp 65001 >nul
call "%~dp0PCL2-一键恢复下载.bat" /restore "%~1"
exit /b %errorlevel%