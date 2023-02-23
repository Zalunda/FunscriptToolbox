using CommandLine;
using FunscriptToolbox.InstallationFiles;
using FunscriptToolbox.Properties;
using log4net;
using log4net.Util;
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
            CreateUseCaseFolder("FSTB-CreateSubtitles", "--FSTB-CreateSubtitles", Resources.CreateSubtitles_bat);
            CreateUseCaseFolder("FSTB-PrepareScriptForRelease", "--FSTB-PrepareScriptForRelease", Resources.PrepareScriptForRelease_bat);
            CreateUseCaseFolder("FSTB-PrepareVideoForOFS", "--FSTB-PrepareVideoForOFS", Resources.PrepareVideoForOFS_bat);
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
                    CreateFileWithReplace(Path.Combine(extensionFolder, "server_connection.lua"), Resources.server_connection_lua);
                    CreateFileWithReplace(Path.Combine(extensionFolder, "main.lua"), Resources.main_lua);
                }
            }
            else
            {
                WriteInfo($@"Skipping OpenFunscripter plugin installation since OFS cannot be found.", ConsoleColor.DarkGray);
            }
        }

        private void CreateUseCaseFolder(string folderName, string baseScriptName, string scriptContent)
        {
            WriteInfo($@"Creating use case folder '{folderName}'.");
            Directory.CreateDirectory(folderName);
            var scriptVersion = Regex.Match(scriptContent, "ScriptVersion:(?<Version>[0-9.]*)");
            var finalScriptName = scriptVersion.Success 
                ? $"{baseScriptName}.{scriptVersion.Groups["Version"].Value}.bat" 
                : $"{baseScriptName}.bat";
            var scriptFullPath = Path.Combine(folderName, finalScriptName);
            if (File.Exists(scriptFullPath))
            {
                WriteInfo($@"Skipping '{finalScriptName}', it already exists.", ConsoleColor.DarkGray);
            }
            else
            {
                CreateFileWithReplace(scriptFullPath, scriptContent);
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
