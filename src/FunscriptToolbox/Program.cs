using CommandLine;
using FunscriptToolbox.AudioSyncVerbs;
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
            int test = 30;

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
                        "subtitles.extractwav",
                        "--verbose",
                        "-r", "JAV*zalunda*.mp4",
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
                    VerbSubtitlesExtractWhisperWav.Options
                    //VerbSubtitlesMergeSrt.Options,
                    //VerbSubtitlesMergeWav.Options,
                    //VerbSubtitlesMergedSrtToChatGPT.Options
                    >(args)
                    .MapResult(
                          (VerbAudioSyncCreateAudioSignature.Options options) => new VerbAudioSyncCreateAudioSignature(options).Execute(),
                          (VerbAudioSyncCreateFunscript.Options options) => new VerbAudioSyncCreateFunscript(options).Execute(),
                          (VerbAudioSyncVerifyFunscript.Options options) => new VerbAudioSyncVerifyFunscript(options).Execute(),
                          (VerbSubtitlesExtractWhisperWav.Options options) => new VerbSubtitlesExtractWhisperWav(options).Execute(),
                          //(VerbSubtitlesMergeSrt.Options options) => new VerbSubtitlesMergeSrt(options).Execute(),
                          //(VerbSubtitlesMergeWav.Options options) => new VerbSubtitlesMergeWav(options).Execute(),
                          //(VerbSubtitlesMergedSrtToChatGPT.Options options) => new VerbSubtitlesMergedSrtToChatGPT(options).Execute(),
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
