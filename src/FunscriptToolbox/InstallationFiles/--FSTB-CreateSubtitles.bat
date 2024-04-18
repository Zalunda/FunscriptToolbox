@echo off
REM ScriptVersion:1.5

set "path=[[FunscriptToolboxFolder]];%path%"

start:
echo --- subtitles.create ---
"FunscriptToolbox.exe" ^
		subtitles.create ^
		--config ".\--FSTB-SubtitleGeneratorConfig.json" ^
		--verbose ^
		"*.mp4"
pause
REM Remove REM from the start of the next line to have a looping script (i.e. run tool, press space, run tool, ...)
REM goto start:
