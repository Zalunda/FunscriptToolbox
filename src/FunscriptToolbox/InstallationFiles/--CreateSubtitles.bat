@echo off
REM ScriptVersion: 1.0

echo How to use:
echo see: https://discuss.eroscripts.com/t/how-to-create-subtitles-for-a-scene-even-if-you-dont-understand-the-language/90168
echo.

set "path=[[FunscriptToolboxFolder]];%path%"

echo --- subtitles.video2vadsrt ---
"FunscriptToolbox.exe" ^
		subtitles.video2vadsrt ^
		--suffix ".temp.vad" ^
		"*.mp4"
	
REM ... Create file .temp.perfect-vad.srt with SubtitleEdit ...
REM ... Rename .temp.vad.wav to .temp.perfect-vad.wav

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

REM ... Use whisper to transcribe the whisper.wav files with the following setting:
REM      Model: large-v2
REM      Language: Japanese (or other)
REM      URL: empty
REM      Upload Files: *.whisper.wav (you can add multiple files)
REM      Task: Transcribe
REM      VAD: none   (it's important to set to None)

REM ... Rename .srt files produced by whisper to ".whisper.srt"

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
