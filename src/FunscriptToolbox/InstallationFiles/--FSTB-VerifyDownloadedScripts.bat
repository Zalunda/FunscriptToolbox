@echo off
REM ScriptVersion:1.0

echo How to use:
echo 1. Move scene.funscript, scene.asig (if .funscript doesn’t include audiosignature) and scene.mp4 to this folder.
echo 2. Start this script.
echo 3a. If your video version is same as the scripter’s version, you’ll see: Audio signatures are SYNCHRONIZED. Script is GOOD.
echo 3b. If your version is different, you’ll see the offsets and this message: Creating synchronized version of the funscript.
echo     and a synchronized version script will have been created for you.
echo.

set "path=[[FunscriptToolboxFolder]];%path%"

echo --- motionvectors.prepare ---
FunscriptToolbox.exe ^
		 audiosync.verifyfunscript ^
		--fix ^
		*.funscript

PAUSE
