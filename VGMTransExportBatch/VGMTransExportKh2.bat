@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "CLI=%~dp0vgmtrans-cli.exe"

if not exist "%CLI%" (
  echo Error: vgmtrans-cli.exe was not found next to this batch file.
  exit /b 1
)

if "%~1"=="" (
  echo Usage:
  echo   Drag one or more KH2 .wd / .bgm files onto this batch file
  echo   or run:
  echo   %~nx0 input1 [input2 ...]
  echo.
  echo Output:
  echo   A folder named "vgmtrans-output" will be created next to the first input file.
  exit /b 1
)

set "FIRST_INPUT=%~1"
set "SECOND_INPUT=%~2"
set "OUTPUT_DIR=%~dp1vgmtrans-output"
set "INPUTS=%*"

if "%SECOND_INPUT%"=="" (
  set "PAIR_FILE="
  for /f "usebackq delims=" %%P in (`powershell -NoProfile -Command "$p = [System.IO.Path]::GetFullPath('%FIRST_INPUT%'); $dir = Split-Path -LiteralPath $p; $name = [System.IO.Path]::GetFileNameWithoutExtension($p); $ext = [System.IO.Path]::GetExtension($p).ToLowerInvariant(); if ($ext -eq '.wd' -and $name -match '^wave(\d+)$') { $n = [int]$matches[1]; $pair = Join-Path $dir ('music{0}.bgm' -f $n); if (Test-Path -LiteralPath $pair) { $pair } } elseif ($ext -eq '.bgm' -and $name -match '^music(\d+)$') { $n = [int]$matches[1]; $pair = Join-Path $dir ('wave{0:D4}.wd' -f $n); if (Test-Path -LiteralPath $pair) { $pair } }"`) do set "PAIR_FILE=%%P"
  if defined PAIR_FILE (
    echo Auto-added matching file: "!PAIR_FILE!"
    set "INPUTS=%INPUTS% "!PAIR_FILE!""
  )
)

if not exist "%OUTPUT_DIR%" (
  mkdir "%OUTPUT_DIR%"
)

echo Exporting with VGMTrans CLI...
echo Output folder: "%OUTPUT_DIR%"
echo.

"%CLI%" %INPUTS% -o "%OUTPUT_DIR%"
set "EXIT_CODE=%ERRORLEVEL%"

if not "%EXIT_CODE%"=="0" (
  echo.
  echo VGMTrans export failed with exit code %EXIT_CODE%.
  exit /b %EXIT_CODE%
)

echo.
echo Finished.
echo Exported files are in:
echo   "%OUTPUT_DIR%"

exit /b 0
