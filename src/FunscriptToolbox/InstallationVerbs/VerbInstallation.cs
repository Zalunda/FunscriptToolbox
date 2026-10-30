using CommandLine;
using FunscriptToolbox.Properties;
using FunscriptToolbox.SubtitlesVerbs;
using log4net;
using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.MotionVectorsVerbs
{
    internal class VerbInstallation : Verb
    {
        private static readonly ILog rs_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly Encoding UTF8NoBOM = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        [Verb("installation", aliases: new[] { "inst" }, HelpText = "Installation and creation of multiple 'use-case' folders.")]
        public class Options : OptionsBase
        {
            [Option('o', "override", Required = false, HelpText = "Override files content, even if they exists", Default = false)]
            public bool OverrideFileContent { get; set; }
        }

        private readonly Options r_options;

        public VerbInstallation(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            bool isWindows = OperatingSystem.IsWindows();

            CreateScriptUseCaseFolder("FSTB-CreateSubtitles2025", "--FSTB-CreateSubtitles", isWindows,
                Resources.FSTB_CreateSubtitles_bat, Resources.FSTB_CreateSubtitles_sh);
            CreateScriptUseCaseFolder("FSTB-CreateSubtitles2025", "--FSTB-GenericCmd", isWindows,
                Resources.FSTB_GenericCmd_bat, Resources.FSTB_GenericCmd_sh);

            CreateUseCaseFolder("FSTB-CreateSubtitles2025", "--FSTB-SubtitleGenerator", ".config", SubtitleGeneratorConfigExample.GetExample(), Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025", "--FSTB-SubtitleGenerator.private", ".config", SubtitleGeneratorPrivateConfig.GetExample(), Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025", "--FSTB-SubtitleGenerator", ".override.config", Resources.__FSTB_SubtitleGenerator_override, Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025\\Staging", "--FSTB-SubtitleGenerator", ".override.config", Resources.__FSTB_SubtitleGenerator_Staging_override, Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025\\ManualHQWorkflow", "--FSTB-SubtitleGenerator", ".override.config", Resources.__FSTB_SubtitleGenerator_ManualHQWorkflow_override, Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025\\ManualHQWorkflow-G4", "--FSTB-SubtitleGenerator", ".override.config", Resources.__FSTB_SubtitleGenerator_ManualHQWorkflow_G4_override, Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025\\ManualHQWorkflow-G4\\--Multi-Speakers", "--FSTB-SubtitleGenerator", ".override.config", Resources.__FSTB_SubtitleGenerator_ManualHQWorkflow_G4_MultiSpeakers_override, Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025\\AutomaticHQWorkflow", "--FSTB-SubtitleGenerator", ".override.config", Resources.__FSTB_SubtitleGenerator_AutomaticHQWorkflow_override, Encoding.UTF8);

            CreateScriptUseCaseFolder("FSTB-PrepareScriptForRelease", "--FSTB-PrepareScriptForRelease", isWindows,
                Resources.FSTB_PrepareScriptForRelease_bat, Resources.FSTB_PrepareScriptForRelease_sh);
            CreateScriptUseCaseFolder("FSTB-PrepareScriptForRelease", "--FSTB-GenericCmd", isWindows,
                Resources.FSTB_GenericCmd_bat, Resources.FSTB_GenericCmd_sh);

            CreateScriptUseCaseFolder("FSTB-VerifyDownloadedScripts", "--FSTB-VerifyDownloadedScripts", isWindows,
                Resources.FSTB_VerifyDownloadedScripts_bat, Resources.FSTB_VerifyDownloadedScripts_sh);
            CreateScriptUseCaseFolder("FSTB-VerifyDownloadedScripts", "--FSTB-GenericCmd", isWindows,
                Resources.FSTB_GenericCmd_bat, Resources.FSTB_GenericCmd_sh);

            return 0;
        }

        private void CreateScriptUseCaseFolder(string folderName, string baseFileName, bool isWindows, string batContent, string shContent)
        {
            if (isWindows)
            {
                CreateUseCaseFolder(folderName, baseFileName, ".bat", batContent, UTF8NoBOM);
            }
            else
            {
                CreateUseCaseFolder(folderName, baseFileName, ".sh", shContent, UTF8NoBOM, isExecutable: true);
            }
        }

        private void CreateUseCaseFolder(string folderName, string baseFileName, string extension, string fileContent, Encoding encoding, bool isExecutable = false)
        {
            var normalizedFolderName = folderName.Replace('\\', Path.DirectorySeparatorChar);
            if (!Directory.Exists(normalizedFolderName))
            {
                WriteInfo($@"Creating use case folder '{normalizedFolderName}'.");
                Directory.CreateDirectory(normalizedFolderName);
            }
            var scriptVersion = Regex.Match(fileContent, "ScriptVersion:(?<Version>[0-9.]*)");
            var finalFileName = scriptVersion.Success
                ? $"{baseFileName}.{scriptVersion.Groups["Version"].Value}{extension}"
                : $"{baseFileName}{extension}";
            var scriptFullPath = Path.Combine(normalizedFolderName, finalFileName);
            if (!r_options.OverrideFileContent && File.Exists(scriptFullPath))
            {
                WriteInfo($@"Skipping '{finalFileName}', it already exists.", ConsoleColor.DarkGray);
            }
            else
            {
                WriteInfo($@"Creating '{finalFileName}'.");
                CreateFileWithReplace(scriptFullPath, fileContent, encoding, isExecutable);
            }
        }

        private static void CreateFileWithReplace(string path, string content, Encoding encoding, bool isExecutable = false)
        {
            var funscriptToolboxExe = Assembly.GetExecutingAssembly().Location;
            var funscriptToolboxFolder = Path.GetDirectoryName(funscriptToolboxExe) ?? ".";
            File.WriteAllText(
                path,
                content
                .Replace("[[FunscriptToolboxExePathInLuaFormat]]", funscriptToolboxExe.Replace(@"\", @"\\"))
                .Replace("[[FunscriptToolboxFolder]]", funscriptToolboxFolder),
                encoding);

            if (isExecutable && !OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(path,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch
                {
                    // Ignore permissions error if running on a non-POSIX filesystem mount
                }
            }
        }
    }
}
