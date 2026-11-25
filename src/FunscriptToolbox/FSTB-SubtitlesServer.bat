@echo off
REM Runs FunscriptToolbox as a subtitles server (web UI on port 5055, reachable from the local network), with a console.
REM Open http://<name or address of this machine>:5055 in a browser (on this machine: http://localhost:5055).
REM The first time, Windows asks for administrator rights (UAC prompt): the application adds the URL reservation
REM and a firewall rule "FunscriptToolbox 5055" (local network only). If refused, it shows the commands to run by hand.
REM Warning: there is no password, use it only on a trusted network.

REM [TO CHANGE] The folder of your subtitles configuration (the one with --FSTB-SubtitleGenerator.config, ex. the
REM 'FSTB-CreateSubtitles2025' folder created by the installation), without '\' at the end. Ex.
REM set "WORK_FOLDER=D:\Videos\FSTB-CreateSubtitles2025"
set "WORK_FOLDER="
set "CONFIG=--FSTB-SubtitleGenerator.config"

if not defined WORK_FOLDER (
    echo [ERROR] WORK_FOLDER is not set: edit "%~f0" and set WORK_FOLDER to the folder of your subtitles configuration.
    pause
    exit /b 1
)
cd /d "%WORK_FOLDER%" || (
    echo [ERROR] The folder "%WORK_FOLDER%" doesn't exist.
    pause
    exit /b 1
)
if not exist "%CONFIG%" (
    echo [ERROR] "%CONFIG%" not found in "%WORK_FOLDER%".
    pause
    exit /b 1
)

"%~dp0FunscriptToolbox.exe" ^
		subtitles.create ^
		--config "%CONFIG%" ^
		--recursive ^
		--verbose ^
		--autovseq ^
		--runasserver *:5055 ^
		"*.mp4" "*.vseq"
pause
