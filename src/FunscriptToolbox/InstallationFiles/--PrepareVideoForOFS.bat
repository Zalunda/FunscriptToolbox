@echo off
REM ScriptVersion:1.0

echo How to use:
echo 1. Move scene-to-script.mp4 to this folder.
echo 2. Start this script.
echo 3. It will starts by creating a scene-to-script.mvs-p-frames.mp4, which is a
echo    version of the video optimized to get stable motionvectors (i.e. only P-frames).
echo    This step is using the ffmpegFilter (value: VRLeft, VRMosaic, 2D or a custom filter) and ffmpegHeight parameters.
echo 4. It will then extracts the motion vector from the video and create scene-to-script.mvs file.
echo 5. Unless --novisual parameter is added, it will then create scene-to-script.mvs-visual.mp4 version 
echo    of scene-to-script.mvs-p-frames.mp4 (i.e. only I-frames).
echo 6. Unless --keeppframes parameter is added, delete scene-to-script.mvs-p-frames.mp4 file, which isn't needed anymore.
echo.

set "path=[[FunscriptToolboxFolder]];%path%"

echo --- motionvectors.prepare ---
FunscriptToolbox.exe ^
		motionvectors.prepare ^
		--ffmpegfilter=VRMosaic ^
		--ffmpegfilterHeight=2048 ^
		*.mp4

PAUSE
