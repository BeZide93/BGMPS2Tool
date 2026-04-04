@echo off
:m
cls
title SEBInfoBatch
echo SEBInfoBatch:
echo -------------
set /p value1=Drag your seb file here to extract it (Press Enter):

if not exist ".\src\SEBInfo\bin\Release\net10.0\SEBInfo.exe" (
  echo SEBInfo.exe was not built yet.
  pause
  exit /b 1
)

".\src\SEBInfo\bin\Release\net10.0\SEBInfo.exe" extract %value1%

echo Process ended.
echo (n will close the batch file)
set /p ans="Would you like to return to the main menu? (Type y for yes or n for no):"
if %ans%==y (
goto m
)

if %ans%== (
goto
)
