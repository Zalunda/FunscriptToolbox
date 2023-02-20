using CommandLine;
using FunscriptToolbox.AudioSyncVerbs;
using FunscriptToolbox.MotionVectorsVerbs;
using FunscriptToolbox.SubtitlesVerb;
using log4net;
using log4net.Appender;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox
{
    class Program
    {
        private static ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        static int HandleParseError(IEnumerable<Error> errs)
        {
            rs_log.Error($"Parse error occured: {string.Join(",", errs.Select(f => f.Tag.ToString()).ToArray())}");
            //handle errors
            return -1;
        }

        static int Main(string[] args)
        {
#if DEBUG
            int test = 41;

            switch (test)
            {
                case 0:
                    args = new[]
                    {
                        "installation"
                    };
                    break;
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
                        "as.cfs",
                        "-s", "JAV - 3DSVR-0647-A - Kurea Hasumi - Strip Club [zalunda].funscript", 
                        "-n", "JAV - 3DSVR-0647 (SLR) - Kurea Hasumi - Strip Club - 20102 [zalunda].mp4"
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
                        "motionvectors.ofspluginserver",
                        "--channelbasefilepath", Environment.ExpandEnvironmentVariables(@"%appdata%\OFS\OFS3_data\extensions\FunscriptToolBox.MotionVectors\Channel-99-"),
                        "--timeout", "300",
                        "--debugmode"
                    };
                    break;
            }
#endif
            try
            {
                UpdateLog4NetFileNameIfAnotherProcessIsRunning();

                rs_log.Info("Application started with arguments:");
                foreach (var arg in args)
                {
                    rs_log.Info($"   {arg}");
                }
                var result = Parser.Default.ParseArguments<
                    VerbInstallation.Options,

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

                    VerbMotionVectorsPrepareFiles.Options,
                    VerbMotionVectorsOFSPluginServer.Options
                    > (args)
                    .MapResult(
                          (VerbInstallation.Options options) => new VerbInstallation(options).Execute(),

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

                          (VerbMotionVectorsPrepareFiles.Options options) => new VerbMotionVectorsPrepareFiles(options).Execute(),
                          (VerbMotionVectorsOFSPluginServer.Options options) => new VerbMotionVectorsOFSPluginServer(options).Execute(),

                          errors => HandleParseError(errors));
                rs_log.Info($"Application closing with return code: {result}");
                return result;
            }
            catch (Exception ex)
            {
                rs_log.Error("Exception occured", ex);
                Console.Error.WriteLine(ex.ToString());
                return -1;
            }
        }

        private static void UpdateLog4NetFileNameIfAnotherProcessIsRunning()
        {
            Environment.SetEnvironmentVariable("suffixe", ".startup");
            log4net.Config.XmlConfigurator.Configure();

            var appender = LogManager.GetRepository().GetAppenders().OfType<FileAppender>().FirstOrDefault();
            var originalFile = appender.File.Replace(".startup", "");

            var currentFile = originalFile;
            var index = 2;
            while (currentFile != null && IsFileLocked(currentFile))
            {
                var suffixe = $".{index++}";
                Environment.SetEnvironmentVariable("suffixe", suffixe);
                currentFile = Path.ChangeExtension(originalFile, $"{suffixe}.log");
            }

            appender.File = currentFile;
            appender.ActivateOptions();
        }

        private static bool IsFileLocked(string fileName)
        {
            try
            {
                if (File.Exists(fileName))
                {
                    using (var stream = File.Open(fileName, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                    }
                    if (new FileInfo(fileName).Length == 0)
                    {
                        File.Delete(fileName);
                    }
                }

                // The file is not locked
                return false;
            }
            catch (IOException)
            {
                // The file is locked by another process
                return true;
            }
        }
    }
}
