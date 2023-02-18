@echo off
REM ScriptVersion: 1.0

echo How to use:
echo 1. Move new-scene.mp4 and new-scene.funscript to this folder.
echo 2. Start this script.
echo 3. It will extracts the audio from new-scene.mp4 and add the audio signature to the new-scene.funscript.
echo 4. It will allows user to synchronize your .funscript to a different version of the video 
echo    (with the audiosync.createfunscript or audiosync.verifyfunscript).
echo.

set "path=[[FunscriptToolboxFolder]];%path%"

echo --- audiosync.createaudiosignature ---
FunscriptToolbox.exe ^
		audiosync.createaudiosignature ^
		*.funscript

PAUSE