@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Set-NocturneFullscreenFix.ps1" -Action Enable %*
set "NocturneExit=%ERRORLEVEL%"
echo.
if not "%NocturneExit%"=="0" echo The fullscreen fix did not finish. Read the message above.
pause
exit /b %NocturneExit%
