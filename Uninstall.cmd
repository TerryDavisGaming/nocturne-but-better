@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-NocturneMod.ps1" -Action Uninstall %*
set "NocturneExit=%ERRORLEVEL%"
echo.
if not "%NocturneExit%"=="0" echo Uninstall did not finish. Read the message above.
pause
exit /b %NocturneExit%
