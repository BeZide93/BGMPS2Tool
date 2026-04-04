@echo off
setlocal
title SEB Blizzard Replace

set "ROOT=%~dp0"
set "EXE=%ROOT%src\SEBInfo\bin\Release\net10.0\SEBInfo.exe"
if exist "%ROOT%.seb_temp_build\SEBInfo.exe" set "EXE=%ROOT%.seb_temp_build\SEBInfo.exe"
set "SEB=%ROOT%se000.seb"

if "%~1"=="" (
  echo Drag a replacement WAV or a folder with 4.wav to 16.wav onto this batch.
  pause
  exit /b 1
)

if not exist "%EXE%" (
  echo SEBInfo.exe was not built yet.
  pause
  exit /b 1
)

if not exist "%SEB%" (
  echo Could not find se000.seb next to this batch.
  pause
  exit /b 1
)

"%EXE%" replaceblizzard "%SEB%" "%~1"

echo.
echo Process ended.
pause
