@echo off
REM ScriptVersion:1.0

set "path=[[FunscriptToolboxFolder]];%path%"

echo --- subtitlesv2.create ---
"FunscriptToolbox.exe" ^
		subtitlesv2.create ^
		--config ".\--FSTB-SubtitleGeneratorConfig.json" ^
		--sourcelanguage "ja" ^
		--verbose ^
		"*.mp4"
pause
