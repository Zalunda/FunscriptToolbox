﻿using log4net;
using System;
using System.IO;
using System.Linq;
using Xabe.FFmpeg;

namespace FunscriptToolbox.SubtitlesVerb
{
    // @echo off
    // 
    // echo --- subtitles.video2vadsrt ---
    // "C:\!dev\GitHub\FunscriptToolbox\src\FunscriptToolbox\bin\Release\FunscriptToolbox" ^
    // 		subtitles.video2vadsrt ^
    // 		--baseextension ".temp.vad" ^
    // 		--recursive ^
    // 		"*.mp4"
    // 		
    // REM ... Create file .temp.perfect-vad.srt with SubtitleEdit ...
    // REM ... Rename .temp.vad.wav to .temp.perfect-vad.wav
    // 
    // echo --- subtitles.srt2singlewav ---
    // "C:\!dev\GitHub\FunscriptToolbox\src\FunscriptToolbox\bin\Release\FunscriptToolbox" ^
    // 		subtitles.srt2vadwav ^
    // 		--baseextension ".whisper" ^
    // 		--recursive ^
    // 		--gapsound "audiocheck.net_whitenoisegaussian.wav" ^
    // 		"*.perfect-vad.srt"
    // 
    // REM ... Use whisper to transcribe the whisper.wav files with the following setting:
    // REM      Model: large-v2
    // REM      Language: Japanese (or other)
    // REM      URL: empty
    // REM      Upload Files: *.whisper.wav (you can add multiple files)
    // REM      Task: Transcribe
    // REM      VAD: none   (it's important to set to None)
    // 
    // REM ... Rename .srt files produced by whisper to ".whisper.srt"
    // 
    // echo --- subtitles.singlewav2srt ---
    // "C:\!dev\GitHub\FunscriptToolbox\src\FunscriptToolbox\bin\Release\FunscriptToolbox" ^
    // 		subtitles.vadwav2srt ^
    // 		--transcribedlanguage "jp" ^
    // 		--recursive ^
    // 		"*.whisper.wav"
    // 
    // echo --- subtitles.srt2gpt ---
    // "C:\!dev\GitHub\FunscriptToolbox\src\FunscriptToolbox\bin\Release\FunscriptToolbox" ^
    // 		subtitles.srt2gpt ^
    // 		--recursive ^
    // 		"*.jp.srt"
    // 
    // REM ... Put all prompts to chatgpt (~30 lines per prompt)
    // 
    // echo --- subtitles.gpt2srt ---
    // "C:\!dev\GitHub\FunscriptToolbox\src\FunscriptToolbox\bin\Release\FunscriptToolbox" ^
    // 		subtitles.gpt2srt ^
    // 		--recursive ^
    // 		"*.gptresults" --force
    // 
    // pause
    // 
    // REM subtitles.srt2singlewav: Add default gaussian inside application


    class VerbSubtitles : Verb
    {
        protected const int SamplingRate = 16000;
        protected const int NbBytesPerSampling = 2;

        public VerbSubtitles(ILog log, OptionsBase options) 
            : base(log, options)
        {
        }

        protected static void ConvertVideoToWav(string inputVideoFilepath, string outputWavFilepath, string extractionParameters)
        {
            if (!File.Exists(inputVideoFilepath))
            {
                throw new ArgumentException($"Can't read video file '{inputVideoFilepath}', file doesn't exists");
            }

            IMediaInfo mediaInfo = FFmpeg.GetMediaInfo(inputVideoFilepath).GetAwaiter().GetResult();
            IStream audioStream = mediaInfo.AudioStreams.FirstOrDefault();
            FFmpeg.Conversions.New()
                .AddStream(audioStream)
                .SetOverwriteOutput(true)
                .AddParameter($"-acodec pcm_s16le -ac 1 -ar {SamplingRate} {extractionParameters}")
                .SetOutput(outputWavFilepath)
                .Start()
                .Wait();
        }

        protected void ConvertPcmToWav(string inputPcmFilepath, string outputWavFilepath)
        {
            if (!File.Exists(inputPcmFilepath))
            {
                throw new ArgumentException($"Can't read .pcm file '{inputPcmFilepath}', file doesn't exists");
            }

            FFmpeg.Conversions.New()
                .SetOverwriteOutput(true)
                .AddParameter($"-f s16le -ar 16000 -ac 1 -i \"{inputPcmFilepath}\" -acodec pcm_s16le")
                .SetOutput(outputWavFilepath)
                .Start()
                .Wait();
        }

        protected void ConvertWavToPcm(string inputWavFilepath, string outputPcmFilepath)
        {
            if (!File.Exists(inputWavFilepath))
            {
                throw new ArgumentException($"Can't read .wav file '{inputWavFilepath}', file doesn't exists");
            }

            IMediaInfo mediaInfo = FFmpeg.GetMediaInfo(inputWavFilepath).GetAwaiter().GetResult();
            IStream audioStream = mediaInfo.AudioStreams.FirstOrDefault();
            FFmpeg.Conversions.New()
                .AddStream(audioStream)
                .SetOverwriteOutput(true)
                .AddParameter($"-f s16le -acodec pcm_s16le -ac 1 -ar {SamplingRate}")
                .SetOutput(outputPcmFilepath)
                .Start()
                .Wait();
        }

        protected byte[] ConvertWavToPcmData(string inputFilepath)
        {
            var tempPcmFile = Path.GetTempFileName() + ".pcm";
            try
            {
                ConvertWavToPcm(inputFilepath, tempPcmFile);
                return File.ReadAllBytes(tempPcmFile);
            }
            finally
            {
                File.Delete(tempPcmFile);
            }
        }

        protected void ConvertPcmDataToWav(string filename, byte[] data, int offset, int count)
        {
            var tempRawFile = Path.GetTempFileName() + ".pcm";
            try
            {
                using (var writer = File.Create(tempRawFile))
                {
                    writer.Write(data, offset, count);
                }
                ConvertPcmToWav(tempRawFile, filename);
            }
            finally
            {
                File.Delete(tempRawFile);
            }
        }
    }
}
