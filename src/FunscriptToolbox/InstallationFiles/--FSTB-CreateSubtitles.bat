@echo off
REM ScriptVersion:1.4

set "path=[[FunscriptToolboxFolder]];%path%"

echo --- subtitles.create ---
"FunscriptToolbox.exe" ^
		subtitles.create ^
		--config ".\--FSTB-SubtitleGeneratorConfig.json" ^
		--sourcelanguage "ja" ^
		--verbose ^
		"*.mp4"
pause
