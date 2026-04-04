@echo off
setlocal
title KH2FM PS2 BGM Replacement Tool

set "tool=%~dp0BGMInfo.exe"

if not exist "%tool%" (
  echo BGMInfo.exe was not found in this folder.
  echo Please keep all tool files together inside BGMPS2Tool.
  echo.
  pause
  exit /b 1
)

if "%~1"=="" (
  echo Drag and drop one or more WAV files onto this batch.
  echo.
  echo Each WAV must be next to its matching PS2 files:
  echo   musicXXX.bgm
  echo   waveXXXX.wd
  echo.
  echo Example:
  echo   music188.ps2.wav
  echo   music188.bgm
  echo   wave0188.wd
  echo.
  echo The rebuilt files will be written to an "output" folder next to the WAV.
  echo.
  pause
  exit /b 1
)

set "exit_code=0"

:loop
if "%~1"=="" goto done

echo ============================================================
echo Processing:
echo   %~f1
echo.
"%tool%" replacewav "%~f1"
if errorlevel 1 set "exit_code=%errorlevel%"
echo.
shift
goto loop

:done
if not "%exit_code%"=="0" (
  echo One or more replacements failed.
  pause
  exit /b %exit_code%
)

echo All replacements finished.
pause
exit /b 0
