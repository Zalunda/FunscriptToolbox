@echo off
REM ScriptVersion:1.3

echo How to use:
echo see: https://discuss.eroscripts.com/t/how-to-create-subtitles-for-a-scene-even-if-you-dont-understand-the-language/90168
echo.

set "path=[[FunscriptToolboxFolder]];%path%"

echo --- subtitles.video2vadsrt ---
"FunscriptToolbox.exe" ^
		subtitles.video2vadsrt ^
		--suffix ".temp.vad" ^
		"*.mp4"

echo.
echo --- subtitles.srt2wavchunks ---
"FunscriptToolbox.exe" ^
		subtitles.srt2wavchunks ^
		"*.perfect-vad.srt"

echo.
echo --- subtitles.srt2singlewav ---
"FunscriptToolbox.exe" ^
		subtitles.srt2vadwav ^
		--suffix ".whisper" ^
		"*.perfect-vad.srt"

echo.
echo --- subtitles.wavchunks2srt ---
"FunscriptToolbox.exe" ^
		subtitles.wavchunks2srt ^
		--suffix ".whisper.chunks" ^
		--verbose ^
		"*.perfect-vad.srt"

echo.
echo --- subtitles.gpt2srt ---
"FunscriptToolbox.exe" ^
		subtitles.gpt2srt ^
		"*.gptresults"

echo.
echo --- subtitles.srt2gpt ---
"FunscriptToolbox.exe" ^
		subtitles.srt2gpt ^
		"*whisper.jp.srt"

echo.
echo --- subtitles.singlewav2srt ---
"FunscriptToolbox.exe" ^
		subtitles.vadwav2srt ^
		--suffix ".jp" ^
		--verbose ^
		"*.whisper.wav"

pause
