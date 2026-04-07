@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
powershell.exe -ExecutionPolicy Bypass -File "%SCRIPT_DIR%MakeMidiDensitySweep.ps1" %*
set "EXIT_CODE=%ERRORLEVEL%"
endlocal & exit /b %EXIT_CODE%
