using CommandLine;
using FunscriptToolbox.AudioSyncVerbs;
using FunscriptToolbox.Core;
using FunscriptToolbox.MotionVectorsVerbs;
using FunscriptToolbox.SubtitlesVerbs;
using log4net;
using log4net.Appender;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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
            int test = 50;

            switch (test)
            {
                case 0:
                    Directory.CreateDirectory(@"InstallationTest");
                    Environment.CurrentDirectory = @"InstallationTest";
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
                    Environment.CurrentDirectory = @"P:\";
                    args = new[]
                    {
                        "as.cfs",
                        "--verbose",
                        "--minimumMatchLength", "20",
                        "--nbLocationsPerMinute", "3",
                        "-i", "VRKM-722.mp4",
                        "-o", "VRKM-722-*.mp4"
                    };
                    break;
                case 11:
                    args = new[]
                    {
                        "as.cfs",
                        "-i", "NaughtyAmericaVR - 2017-04-20 - Melissa Moore - Wake and Bake [zalunda].funscript",
                        "-o", "mygfmelissaseth_vrdesktophd.asig",
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

                case 40:
                    args = new[]
                    {
                        "motionvectors.prepare",
                        //"--verbose",
                        @"Position-Doggy-MenStanding-C.mp4",
                    };
                    break;
                case 41:
                    args = new[]
                    {
                        "motionvectors.ofspluginserver",
                        "--channelbasefilepath", Environment.ExpandEnvironmentVariables(@"%appdata%\OFS\OFS3_data\extensions\FunscriptToolBox.MotionVectors.V2BETA\Channel-999-"),
                        "--channellockfilepath", Environment.ExpandEnvironmentVariables(@"%appdata%\OFS\OFS3_data\extensions\FunscriptToolBox.MotionVectors.V2BETA\Channel-999.lock"),
                        "--timeout", "300",
                        "--debugmode",
                        "--skipupdate"
                    };
                    break;

                case 50:
                    // Environment.CurrentDirectory = @"P:\Adult\Tools\FunscriptToolbox\FSTB-CreateSubtitles2025";
                    var prefix = "SAVR-681*";
                    args = new[]
                    {
                        "subtitles.create",
                        "--verbose",
                        "--recursive",
                        "--skipupdate",
                        "--config", ".\\--FSTB-SubtitleGenerator.config",
                        $"{prefix}.vseq",
                        $"{prefix}.mp4"
                    };
                    break;

                case 51:
                    Directory.CreateDirectory(@"InstallationTest");
                    Environment.CurrentDirectory = "InstallationTest";
                    new VerbInstallation(new VerbInstallation.Options() { OverrideFileContent = true }).Execute();
                    File.Copy("--FSTB-SubtitleGenerator.private.config", @"FSTB-CreateSubtitles2025\--FSTB-SubtitleGenerator.private.config", true);
                    File.WriteAllText(
                        @"FSTB-CreateSubtitles2025\--FSTB-SubtitleGenerator.override.config",
                        File.ReadAllText(@"FSTB-CreateSubtitles2025\--FSTB-SubtitleGenerator.override.config")
                            .Replace("//\"ApplicationFullPath\"", "\"ApplicationFullPath\"")
                            .Replace("[TOREPLACE-WITH-PathToPurfview]", "D:\\\\OthersPrograms\\\\SubtitleEditor\\\\Whisper"),
                        Encoding.UTF8);
                    File.WriteAllText(
                        @"FSTB-CreateSubtitles2025\Staging\testStaging.vseq",
                        "unfound.mp4");
                    File.WriteAllText(
                        @"FSTB-CreateSubtitles2025\ManualHQWorkflow\testManualHQWorkflow.vseq",
                        "unfound.mp4");
                    File.WriteAllText(
                        @"FSTB-CreateSubtitles2025\AutomaticHQWorkflow\testAutomaticHQWorkflow.vseq",
                        "unfound.mp4");
                    Environment.CurrentDirectory = "FSTB-CreateSubtitles2025";
                    args = new[]
                    {
                        "subtitles.create",
                        "--verbose",
                        "--recursive",
                        "--skipupdate",
                        "--config", ".\\--FSTB-SubtitleGenerator.config",
                        "*.vseq", "*.mp4"
                    };
                    break;

                case 52:
                    var filename = "Test.funscript";
                    var minSpeed = 20;
                    var maxSpeed = 600;
                    foreach (var scale in new[] { 0.5, 1.5 })
                    {
                        var funscript = Funscript.FromFile(filename);
                        var wipActions = WIPFunscriptActionCollection.FromFunscriptActions(funscript.Actions);
                        wipActions.Scale(scale); // scale : 0.5, 0.8, 1.2, etc
                        wipActions.SetMinSpeed(minSpeed);
                        wipActions.SetMaxSpeed(maxSpeed);
                        funscript.Actions = wipActions.ToFunscriptActions();
                        funscript.Save($"Test.{scale}.{minSpeed}.{maxSpeed}.funscript");
                    }
                    return 0;
            }
#endif
            try
            {
                UpdateLog4NetFileNameIfAnotherProcessIsRunning();
                Console.OutputEncoding = Encoding.UTF8;

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

                    VerbSubtitlesCreate.Options,

                    VerbMotionVectorsPrepareFiles.Options,
                    VerbMotionVectorsOFSPluginServer.Options
                    >(args)
                    .MapResult(
                          (VerbInstallation.Options options) => new VerbInstallation(options).Execute(),

                          (VerbAudioSyncCreateAudioSignature.Options options) => new VerbAudioSyncCreateAudioSignature(options).Execute(),
                          (VerbAudioSyncCreateFunscript.Options options) => new VerbAudioSyncCreateFunscript(options).Execute(),
                          (VerbAudioSyncVerifyFunscript.Options options) => new VerbAudioSyncVerifyFunscript(options).Execute(),

                          (VerbSubtitlesCreate.Options options) => new VerbSubtitlesCreate(options).Execute(),

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
