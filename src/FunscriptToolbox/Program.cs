using CommandLine;
using FunscriptToolbox.AudioSyncVerbs;
using FunscriptToolbox.MotionVectorsVerbs;
using FunscriptToolbox.SubtitlesVerb;
using log4net;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox
{
    class Program
    {
        private static readonly ILog rs_log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        static int HandleParseError(IEnumerable<Error> errs)
        {
            //handle errors
            return -1;
        }

        static int Main(string[] args)
        {
#if DEBUG
            int test = 42;

            switch (test)
            {
                case 1:
                    args = new[]
                    {
                        "audiosync.createaudiosignature"
                    };
                    break;
                case 2:
                    args = new[]
                    {
                        "audiosync.createaudiosignature",
                        "--force",
                        "*.mp4",
                        "*.funscript"
                    };
                    break;
                case 10:
                    args = new[]
                    {
                        "as.cfs"
                    };
                    break;
                case 11:
                    args = new[]
                    {
                        "as.cfs",
                        "-s", "NaughtyAmericaVR - 2017-04-20 - Melissa Moore - Wake and Bake [zalunda].funscript",
                        "-n", "mygfmelissaseth_vrdesktophd.asig",
                    };
                    break;

                case 20:
                    args = new[]
                    {
                        "as.vfs"
                    };
                    break;
                case 21:
                    args = new[]
                    {
                        "as.vfs",
                        // "--verbose",
                        "--fix",
                        "*.funscript",
                    };
                    break;

                case 30:
                    args = new[]
                    {
                        "subtitles.video2vadsrt",
                        "--verbose",
                        "--suffix", ".temp.vad",
                        @"*.mp4",
                    };
                    break;
                case 31:
                    args = new[]
                    {
                        "subtitles.srt2wavchunks",
                        "--verbose",
                        "*.perfect-vad.srt",
                    };
                    break;
                case 32:
                    args = new[]
                    {
                        "subtitles.srt2vadwav",
                        "--verbose",
                        "*.temp.perfect-vad.srt"
                    };
                    break;
                case 33:
                    args = new[]
                    {
                        "subtitles.wavchunks2srt",
                        "--verbose",
                        "--suffix", ".whisper.chunks",
                        "*.perfect-vad.srt",
                    };
                    break;
                case 34:
                    args = new[]
                    {
                        "subtitles.vadwav2srt",
                        "--verbose",
                        "--suffix", ".jp",
                        "*.whisper.wav",
                    };
                    break;
                case 35:
                    args = new[]
                    {
                        "subtitles.srt2gpt",
                        "--verbose",
                        "*.whisper.jp.srt", 
                    };
                    break;
                case 36:
                    args = new[]
                    {
                        "subtitles.gpt2srt",
                        "--verbose",
                        "*.gptresults",
                    };
                    break;

                case 40:
                    args = new[]
                    {
                        "motionvectors.prepare",
                        //"--verbose",
                        "Position-CowGirlUpright-MenLaying-A.mp4",
                    };
                    break;
                case 41:
                    args = new[]
                    {
                        "motionvectors.createfunscript",
                        //"--verbose",
                        "Position-CowGirlUpright-MenLaying-A.mvs",
                    };
                    break;
                case 42:
                    args = new[]
                    {
                        "motionvectors.ui",
                        //"--verbose",
                        "Position-Doggy-MenStanding-A.mp4",
                    };
                    break;
            }

#endif
            try
            {
                rs_log.Info("Application started with arguments:");
                foreach (var arg in args)
                {
                    rs_log.Info($"   {arg}");
                }
                var result = Parser.Default.ParseArguments<
                    VerbAudioSyncCreateAudioSignature.Options,
                    VerbAudioSyncCreateFunscript.Options,
                    VerbAudioSyncVerifyFunscript.Options,

                    VerbSubtitlesVideo2VADSrt.Options,
                    VerbSubtitlesSrt2VADWav.Options,
                    VerbSubtitlesSrt2WavChunks.Options,
                    VerbSubtitlesWavChunks2Srt.Options,
                    VerbSubtitlesVADWav2Srt.Options,
                    VerbSubtitlesGPT2Srt.Options,
                    VerbSubtitlesSrt2GPT.Options,

                    VerbMotionVectorsCreateFunscript.Options,
                    VerbMotionVectorsPrepareFiles.Options
                    > (args)
                    .MapResult(
                          (VerbAudioSyncCreateAudioSignature.Options options) => new VerbAudioSyncCreateAudioSignature(options).Execute(),
                          (VerbAudioSyncCreateFunscript.Options options) => new VerbAudioSyncCreateFunscript(options).Execute(),
                          (VerbAudioSyncVerifyFunscript.Options options) => new VerbAudioSyncVerifyFunscript(options).Execute(),

                          (VerbSubtitlesVideo2VADSrt.Options options) => new VerbSubtitlesVideo2VADSrt(options).Execute(),
                          (VerbSubtitlesSrt2VADWav.Options options) => new VerbSubtitlesSrt2VADWav(options).Execute(),
                          (VerbSubtitlesSrt2WavChunks.Options options) => new VerbSubtitlesSrt2WavChunks(options).Execute(),
                          (VerbSubtitlesWavChunks2Srt.Options options) => new VerbSubtitlesWavChunks2Srt(options).Execute(),
                          (VerbSubtitlesVADWav2Srt.Options options) => new VerbSubtitlesVADWav2Srt(options).Execute(),
                          (VerbSubtitlesGPT2Srt.Options options) => new VerbSubtitlesGPT2Srt(options).Execute(),
                          (VerbSubtitlesSrt2GPT.Options options) => new VerbSubtitlesSrt2GPT(options).Execute(),

                          (VerbMotionVectorsCreateFunscript.Options options) => new VerbMotionVectorsCreateFunscript(options).Execute(),
                          (VerbMotionVectorsPrepareFiles.Options options) => new VerbMotionVectorsPrepareFiles(options).Execute(),

                          errors => HandleParseError(errors));
                return result;
            }
            catch (Exception ex)
            {
                rs_log.Error("Exception occured", ex);
                Console.Error.WriteLine(ex.ToString());
                return -1;
            }
        }
    }
}
