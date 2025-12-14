@echo off
REM ScriptVersion:2.1

set "path=[[FunscriptToolboxFolder]];%path%"

:start
echo --- subtitles.create ---
"FunscriptToolbox.exe" ^
		subtitles.create ^
		--config ".\--FSTB-SubtitleGenerator.config" ^
		--recursive ^
		--verbose ^
		--autovseq ^
		"*.mp4" "*.vseq"
pause
REM Remove REM from the start of the next line to have a looping script (i.e. run tool, press space, run tool, ...)
REM goto start
