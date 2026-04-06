@echo off
setlocal

if "%~1"=="" (
  echo Usage:
  echo   Drag a MIDI file onto this batch
  echo   or run: BGMVgmTransDiff.bat musicXXX.mid [waveXXXX.sf2]
  exit /b 1
)

set "SCRIPT_DIR=%~dp0"

if "%~2"=="" (
  "%SCRIPT_DIR%BGMInfo.exe" vgmtransdiff "%~1"
) else (
  "%SCRIPT_DIR%BGMInfo.exe" vgmtransdiff "%~1" "%~2"
)
