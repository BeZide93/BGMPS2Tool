@echo off
:m
cls
title BGMInfoBatch
echo BGMInfoBatch:
echo -------------
set /p value1=Drag your bgm file here to extract it (Press Enter):

if not exist ".\src\BGMInfo\bin\Release\net10.0\BGMInfo.exe" (
  echo BGMInfo.exe was not built yet.
  pause
  exit /b 1
)

".\src\BGMInfo\bin\Release\net10.0\BGMInfo.exe" extract %value1%

echo Process ended.
echo (n will close the batch file)
set /p ans="Would you like to return to the main menu? (Type y for yes or n for no):"
if %ans%==y (
goto m
)

if %ans%== (
goto
)
