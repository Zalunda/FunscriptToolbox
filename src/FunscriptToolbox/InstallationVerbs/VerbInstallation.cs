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

        [Verb("installation", aliases: new[] { "inst" }, HelpText = "Installation of the OFSPlugin, creation of multiples 'use-case' folder.")]
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
            CreateUseCaseFolder("FSTB-CreateSubtitles2025", "--FSTB-CreateSubtitles", ".bat", Resources.FSTB_CreateSubtitles_bat, UTF8NoBOM);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025", "--FSTB-GenericCmd", ".bat", Resources.FSTB_GenericCmd_bat, UTF8NoBOM);

            CreateUseCaseFolder("FSTB-CreateSubtitles2025", "--FSTB-SubtitleGenerator", ".config", SubtitleGeneratorConfigExample.GetExample(), Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025", "--FSTB-SubtitleGenerator.private", ".config", SubtitleGeneratorPrivateConfig.GetExample(), Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025", "--FSTB-SubtitleGenerator", ".override.config", Resources.__FSTB_SubtitleGenerator_override, Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025\\Staging", "--FSTB-SubtitleGenerator", ".override.config", Resources.__FSTB_SubtitleGenerator_Staging_override, Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025\\ManualHQWorkflow", "--FSTB-SubtitleGenerator", ".override.config", Resources.__FSTB_SubtitleGenerator_ManualHQWorkflow_override, Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025\\ManualHQWorkflow-G4", "--FSTB-SubtitleGenerator", ".override.config", Resources.__FSTB_SubtitleGenerator_ManualHQWorkflow_G4_override, Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025\\ManualHQWorkflow-G4\\--Multi-Speakers", "--FSTB-SubtitleGenerator", ".override.config", Resources.__FSTB_SubtitleGenerator_ManualHQWorkflow_G4_MultiSpeakers_override, Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025\\AutomaticHQWorkflow", "--FSTB-SubtitleGenerator", ".override.config", Resources.__FSTB_SubtitleGenerator_AutomaticHQWorkflow_override, Encoding.UTF8);

            CreateUseCaseFolder("FSTB-PrepareScriptForRelease", "--FSTB-PrepareScriptForRelease", ".bat", Resources.FSTB_PrepareScriptForRelease_bat, UTF8NoBOM);
            CreateUseCaseFolder("FSTB-PrepareScriptForRelease", "--FSTB-GenericCmd", ".bat", Resources.FSTB_GenericCmd_bat, UTF8NoBOM);

            CreateUseCaseFolder("FSTB-VerifyDownloadedScripts", "--FSTB-VerifyDownloadedScripts", ".bat", Resources.FSTB_VerifyDownloadedScripts_bat, UTF8NoBOM);
            CreateUseCaseFolder("FSTB-VerifyDownloadedScripts", "--FSTB-GenericCmd", ".bat", Resources.FSTB_GenericCmd_bat, UTF8NoBOM);

            return 0;
        }

        private void CreateUseCaseFolder(string folderName, string baseFileName, string extension, string fileContent, Encoding encoding)
        {
            if (!Directory.Exists(folderName))
            {
                WriteInfo($@"Creating use case folder '{folderName}'.");
                Directory.CreateDirectory(folderName);
            }
            var scriptVersion = Regex.Match(fileContent, "ScriptVersion:(?<Version>[0-9.]*)");
            var finalFileName = scriptVersion.Success 
                ? $"{baseFileName}.{scriptVersion.Groups["Version"].Value}{extension}" 
                : $"{baseFileName}{extension}";
            var scriptFullPath = Path.Combine(folderName, finalFileName);
            if (!r_options.OverrideFileContent && File.Exists(scriptFullPath))
            {
                WriteInfo($@"Skipping '{finalFileName}', it already exists.", ConsoleColor.DarkGray);
            }
            else
            {
                WriteInfo($@"Creating '{finalFileName}'.");
                CreateFileWithReplace(scriptFullPath, fileContent, encoding);
            }
        }

        private static void CreateFileWithReplace(string path, string content, Encoding encoding)
        {
            var funscriptToolboxExe = Assembly.GetExecutingAssembly().Location;
            var funscriptToolboxFolder = Path.GetDirectoryName(funscriptToolboxExe) ?? ".";
            File.WriteAllText(
                path,
                content
                .Replace("[[FunscriptToolboxExePathInLuaFormat]]", funscriptToolboxExe.Replace(@"\", @"\\")) // Need to double the backslash for lua
                .Replace("[[FunscriptToolboxFolder]]", funscriptToolboxFolder),
                encoding);
        }
    }
}
