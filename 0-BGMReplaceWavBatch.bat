@echo off
setlocal
title BGM WAV -> PS2 Pair

if "%~1"=="" (
  echo Drag and drop a WAV file onto this batch.
  echo.
  echo Expected next to the WAV:
  echo   musicXXX.bgm
  echo   waveXXXX.wd
  echo.
  echo Example:
  echo   music059.ps2.wav  ->  output\music059.bgm + output\wave0059.wd
  pause
  exit /b 1
)

set "tool=%~dp0src\BGMInfo\bin\Release\net10.0\BGMInfo.exe"
if exist "%~dp0.bgm_temp_build\BGMInfo.exe" set "tool=%~dp0.bgm_temp_build\BGMInfo.exe"
if not exist "%tool%" (
  echo BGMInfo.exe was not built yet.
  pause
  exit /b 1
)

echo Rebuilding PS2 BGM pair from:
echo   %~1
echo.
"%tool%" replacewav "%~1"
set "exit_code=%errorlevel%"
echo.

if not "%exit_code%"=="0" (
  echo The rebuild failed.
  pause
  exit /b %exit_code%
)

echo Finished.
pause
exit /b 0
