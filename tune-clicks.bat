@echo off
REM Dial the radio click sounds in by ear. Drives the SHIPPED CMM_Decoder.dll, so what you hear
REM here is exactly what the game plays -- there is no second copy of the synthesis to drift.
cd /d "%~dp0CMM_Decoder"
if not exist tune_clicks.exe (
  echo Building the tuner...
  gcc -O2 -o tune_clicks.exe tune_clicks.c dist\CMM_Decoder.dll -lm -lwinmm || goto :err
)
copy /y dist\CMM_Decoder.dll . >nul
tune_clicks.exe
if exist clicks.tuned.xml (
  echo.
  echo Values were written to CMM_Decoder\clicks.tuned.xml
  echo Paste them over the matching lines in Config\radio.xml -- it hot-reloads, no restart.
)
pause
exit /b 0
:err
echo Could not build. Needs gcc on PATH ^(the same one that builds the DLL^).
pause
