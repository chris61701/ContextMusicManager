@echo off
REM Tune the radio's clicks by ear, outside the game. Drives the SHIPPED CMM_Decoder.dll, so what
REM you hear here is exactly what the game plays. W saves into the game's Config\radio.xml (found
REM automatically, or pass its path as the first argument); the game reloads it live.
cd /d "%~dp0CMM_Decoder"
echo Building the tuner...
gcc -O2 -o tune_clicks.exe tune_clicks.c dist\CMM_Decoder.dll -lm -lwinmm || goto :err
copy /y dist\CMM_Decoder.dll . >nul
tune_clicks.exe %*
del CMM_Decoder.dll >nul 2>&1
exit /b 0
:err
echo Could not build. Needs gcc on PATH (the same one that builds the DLL).
pause
