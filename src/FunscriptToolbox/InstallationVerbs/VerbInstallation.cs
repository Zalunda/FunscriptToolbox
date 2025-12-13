using CommandLine;
using FunscriptToolbox.InstallationFiles;
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
            var prefixExamples = "Default-2.0.18-";

            InstallOFSPlugin();
            CreateUseCaseFolder("FSTB-CreateSubtitles2025", "--FSTB-CreateSubtitles", ".bat", Resources.FSTB_CreateSubtitles_bat, UTF8NoBOM);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025", "--FSTB-GenericCmd", ".bat", Resources.FSTB_GenericCmd_bat, UTF8NoBOM);

            CreateUseCaseFolder("FSTB-CreateSubtitles2025", "--FSTB-SubtitleGenerator", ".config", SubtitleGeneratorConfigExample.GetExample(), Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025", "--FSTB-SubtitleGenerator.private", ".config", SubtitleGeneratorPrivateConfig.GetExample(), Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025", "--FSTB-SubtitleGenerator", ".override.config", Resources.__FSTB_SubtitleGenerator_override, Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025\\Staging", "--FSTB-SubtitleGenerator", ".override.config", Resources.__FSTB_SubtitleGenerator_Staging_override, Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025\\ManualHQWorkflow", "--FSTB-SubtitleGenerator", ".override.config", Resources.__FSTB_SubtitleGenerator_ManualHQWorkflow_override, Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025\\AutomaticHQWorkflow", "--FSTB-SubtitleGenerator", ".override.config", Resources.__FSTB_SubtitleGenerator_AutomaticHQWorkflow_override, Encoding.UTF8);

            CreateUseCaseFolder("FSTB-CreateSubtitles2025", prefixExamples + "--FSTB-SubtitleGenerator", ".config", SubtitleGeneratorConfigExample.GetExample(), Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025", prefixExamples + "--FSTB-SubtitleGenerator", ".override.config", Resources.__FSTB_SubtitleGenerator_override, Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025", prefixExamples + "--FSTB-SubtitleGenerator.Stagging", ".override.config", Resources.__FSTB_SubtitleGenerator_Staging_override, Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025", prefixExamples + "--FSTB-SubtitleGenerator.ManualHQWorkflow", ".override.config", Resources.__FSTB_SubtitleGenerator_ManualHQWorkflow_override, Encoding.UTF8);
            CreateUseCaseFolder("FSTB-CreateSubtitles2025", prefixExamples + "--FSTB-SubtitleGenerator.AutomaticHQWorkflow", ".override.config", Resources.__FSTB_SubtitleGenerator_AutomaticHQWorkflow_override, Encoding.UTF8);

            CreateUseCaseFolder("FSTB-PrepareScriptForRelease", "--FSTB-PrepareScriptForRelease", ".bat", Resources.FSTB_PrepareScriptForRelease_bat, UTF8NoBOM);
            CreateUseCaseFolder("FSTB-PrepareScriptForRelease", "--FSTB-GenericCmd", ".bat", Resources.FSTB_GenericCmd_bat, UTF8NoBOM);

            CreateUseCaseFolder("FSTB-PrepareVideoForOFS", "--FSTB-PrepareVideoForOFS", ".bat", Resources.FSTB_PrepareVideoForOFS_bat, UTF8NoBOM);
            CreateUseCaseFolder("FSTB-PrepareVideoForOFS", "--FSTB-GenericCmd", ".bat", Resources.FSTB_GenericCmd_bat, UTF8NoBOM);

            CreateUseCaseFolder("FSTB-VerifyDownloadedScripts", "--FSTB-VerifyDownloadedScripts", ".bat", Resources.FSTB_VerifyDownloadedScripts_bat, UTF8NoBOM);
            CreateUseCaseFolder("FSTB-VerifyDownloadedScripts", "--FSTB-GenericCmd", ".bat", Resources.FSTB_GenericCmd_bat, UTF8NoBOM);

            return 0;
        }

        private void InstallOFSPlugin()
        {
            var ofsFolder = Environment.ExpandEnvironmentVariables($@"%appdata%\OFS");
            if (Directory.Exists(ofsFolder))
            {
                foreach (var ofsVersionFullPath in Directory.GetDirectories(ofsFolder))
                {
                    var extensionName = "FunscriptToolBox.MotionVectors";
                    var ofsVersionName = Path.GetFileName(ofsVersionFullPath);

                    WriteInfo($@"Adding extension '{extensionName} to OpenFunscripter folder '<appdata>\OFS\{ofsVersionName}'.");
                    var extensionFolder = Path.Combine(ofsVersionFullPath, "extensions", extensionName);
                    Directory.CreateDirectory(extensionFolder);
                    CreateFileWithReplace(Path.Combine(extensionFolder, "json.lua"), Resources.json_lua, UTF8NoBOM);
                    CreateFileWithReplace(Path.Combine(extensionFolder, "main.lua"), Resources.main_lua, UTF8NoBOM);
                    CreateFileWithReplace(Path.Combine(extensionFolder, "server_connection.lua"), Resources.server_connection_lua, UTF8NoBOM);
                    CreateFileWithReplace(Path.Combine(extensionFolder, "virtual_actions.lua"), Resources.virtual_actions_lua, UTF8NoBOM);
                }
            }
            else
            {
                WriteInfo($@"Skipping OpenFunscripter plugin installation since OFS cannot be found.", ConsoleColor.DarkGray);
            }
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
                .Replace("[[FunscriptToolboxFolder]]", funscriptToolboxFolder)
                .Replace("[[PluginVersion]]", PluginClient.Version), 
                encoding);
        }
    }
}
