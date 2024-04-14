using CommandLine;
using FunscriptToolbox.InstallationFiles;
using FunscriptToolbox.Properties;
using FunscriptToolbox.SubtitlesVerbs;
using log4net;
using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.MotionVectorsVerbs
{
    internal class VerbInstallation : Verb
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("installation", aliases: new[] { "inst" }, HelpText = "Installation of the OFSPlugin, creation of multiples 'use-case' folder.")]
        public class Options : OptionsBase
        {
            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }
        }

        private readonly Options r_options;

        public VerbInstallation(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            InstallOFSPlugin();
            CreateUseCaseFolder("FSTB-CreateSubtitles", "--FSTB-CreateSubtitles", ".bat", Resources.FSTB_CreateSubtitles_bat);
            CreateUseCaseFolder("FSTB-CreateSubtitles", "--FSTB-GenericCmd", ".bat", Resources.FSTB_GenericCmd_bat);
            CreateUseCaseFolder("FSTB-CreateSubtitles", "--FSTB-SubtitleGeneratorConfig", ".json", SubtitleGeneratorConfig.GetExample());
            CreateUseCaseFolder("FSTB-CreateSubtitles", "--FSTB-SubtitleGeneratorConfigExample-1.0", ".json", SubtitleGeneratorConfig.GetExample());

            CreateUseCaseFolder("FSTB-PrepareScriptForRelease", "--FSTB-PrepareScriptForRelease", ".bat", Resources.FSTB_PrepareScriptForRelease_bat);
            CreateUseCaseFolder("FSTB-PrepareScriptForRelease", "--FSTB-GenericCmd", ".bat", Resources.FSTB_GenericCmd_bat);

            CreateUseCaseFolder("FSTB-PrepareVideoForOFS", "--FSTB-PrepareVideoForOFS", ".bat", Resources.FSTB_PrepareVideoForOFS_bat);
            CreateUseCaseFolder("FSTB-PrepareVideoForOFS", "--FSTB-GenericCmd", ".bat", Resources.FSTB_GenericCmd_bat);

            CreateUseCaseFolder("FSTB-VerifyDownloadedScripts", "--FSTB-VerifyDownloadedScripts", ".bat", Resources.FSTB_VerifyDownloadedScripts_bat);
            CreateUseCaseFolder("FSTB-VerifyDownloadedScripts", "--FSTB-GenericCmd", ".bat", Resources.FSTB_GenericCmd_bat);

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
                    CreateFileWithReplace(Path.Combine(extensionFolder, "json.lua"), Resources.json_lua);
                    CreateFileWithReplace(Path.Combine(extensionFolder, "main.lua"), Resources.main_lua);
                    CreateFileWithReplace(Path.Combine(extensionFolder, "server_connection.lua"), Resources.server_connection_lua);
                    CreateFileWithReplace(Path.Combine(extensionFolder, "virtual_actions.lua"), Resources.virtual_actions_lua);
                }
            }
            else
            {
                WriteInfo($@"Skipping OpenFunscripter plugin installation since OFS cannot be found.", ConsoleColor.DarkGray);
            }
        }

        private void CreateUseCaseFolder(string folderName, string baseFileName, string extension, string fileContent)
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
            if (File.Exists(scriptFullPath))
            {
                WriteInfo($@"Skipping '{finalFileName}', it already exists.", ConsoleColor.DarkGray);
            }
            else
            {
                WriteInfo($@"Creating '{finalFileName}'.");
                CreateFileWithReplace(scriptFullPath, fileContent);
            }
        }

        private static void CreateFileWithReplace(string path, string content)
        {
            var funscriptToolboxExe = Assembly.GetExecutingAssembly().Location;
            var funscriptToolboxFolder = Path.GetDirectoryName(funscriptToolboxExe) ?? ".";
            File.WriteAllText(
                path,
                content
                .Replace("[[FunscriptToolboxExePathInLuaFormat]]", funscriptToolboxExe.Replace(@"\", @"\\")) // Need to double the backslash for lua
                .Replace("[[FunscriptToolboxFolder]]", funscriptToolboxFolder)
                .Replace("[[PluginVersion]]", PluginClient.Version));
        }
    }
}
