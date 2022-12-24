using AudioSynchronization;
using CommandLine;
using FunscriptToolbox.Core;
using Hudl.FFmpeg.Command;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;

namespace FunscriptToolbox
{
    internal class Verb
    {
        public class OptionsBase
        {
            [Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.")]
            public bool Verbose { get; set; }

            protected int ValidateMinValue(int value, int minValue)
            { 
                return ValidateValue(value, (v) => v < minValue, $"value cannot be lower then {minValue}");
            }

            protected T ValidateValue<T>(T value, Func<T, bool> func, string message)
            {
                if (func(value)) 
                    throw new ArgumentException(message);
                return value;
            }
        }

        public static UnParserSettings DefaultUnparserSettings => new UnParserSettings { PreferShortName = true, SkipDefault = true };

        public const string ApplicationName = "FunscriptToolbox";

        private readonly ILog r_log;
        private readonly OptionsBase r_options;
        private readonly string r_ffmpegWorkingFolder;
        private readonly CommandConfiguration r_ffmpegConfiguration;
        private readonly AudioTracksAnalyzer r_audioAnalyzer;
        
        public FunscriptVault FunscriptVault { get; }

        public int NbErrors { get; private set; }

        public Verb(ILog log, OptionsBase options)
        {
            var appDataFolder = Environment.ExpandEnvironmentVariables($@"%appdata%\{ApplicationName}");

            r_log = log;
            r_options = options;
            r_ffmpegWorkingFolder = Path.Combine(appDataFolder, "ffmpeg");
            r_ffmpegConfiguration = CommandConfiguration.Create(
                r_ffmpegWorkingFolder,
                Path.Combine(r_ffmpegWorkingFolder, "ffmpeg.exe"),
                Path.Combine(r_ffmpegWorkingFolder, "ffprobe.exe"));
            r_audioAnalyzer = new AudioTracksAnalyzer(r_ffmpegConfiguration);
            this.FunscriptVault = new FunscriptVault(Path.Combine(appDataFolder, "Vault"));
        }

        protected AudioSignature ExtractAudioSignature(string filename)
        {
            UpdateFfmpeg();
            return r_audioAnalyzer.ExtractSignature(filename);
        }

        protected FunscriptAudioSignature Convert(AudioSignature signature)
        {
            return new FunscriptAudioSignature(signature.NbSamplesPerSecond, signature.CompressedSamples);
        }
        protected AudioSignature Convert(FunscriptAudioSignature signature)
        {
            return new AudioSignature(signature.NbSamplesPerSecond, signature.CompressedSamples);
        }

        private void UpdateFfmpeg()
        {
            if (!File.Exists(r_ffmpegConfiguration.FFmpegPath) || !File.Exists(r_ffmpegConfiguration.FFprobePath))
            {
                WriteInfo("ffmpeg/ffprobe missing.");
                Directory.CreateDirectory(r_ffmpegWorkingFolder);
                var archiveFilename = Path.Combine(r_ffmpegWorkingFolder, "ffmpeg-release-essentials.zip");
                var url = $"https://www.gyan.dev/ffmpeg/builds/{Path.GetFileName(archiveFilename)}";
                WriteInfo($"Downloading ffmpeg/ffprobe from '{url}'...");
                using (var client = new WebClient())
                {
                    client.DownloadFile(url, archiveFilename);
                }
                using (var file = ZipFile.Open(archiveFilename, ZipArchiveMode.Read))
                {
                    foreach (var e in file.Entries)
                    {
                        if (e.Name == "ffmpeg.exe")
                        {
                            WriteInfo($"Extraction '{e.FullName}'...");
                            e.ExtractToFile(r_ffmpegConfiguration.FFmpegPath, true);
                        }
                        if (e.Name == "ffprobe.exe")
                        {
                            WriteInfo($"Extraction '{e.FullName}'...");
                            e.ExtractToFile(r_ffmpegConfiguration.FFprobePath, true);
                        }
                    }
                }
            }
        }

        public void WriteInfo(string message = "", ConsoleColor? color = null)
        {
            r_log.Info(message);
            if (color != null) Console.ForegroundColor = color.Value;
            Console.WriteLine(message);
            Console.ResetColor();
        }

        public void WriteVerbose(string message = "")
        {
            r_log.Debug(message);
            if (r_options.Verbose)
            {
                Console.WriteLine(message);
            }                
        }

        public void WriteError(string message = "")
        {
            r_log.Error(message);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(message);
            this.NbErrors++;
        }

        protected static string FormatTimeSpan(TimeSpan value)
        {
            return Regex.Replace(value.ToString(), @"\d{4}$", "");
                
        }

        protected IEnumerable<string> HandleStarAndRecusivity(string filename, bool recursive = false)
        {
            if (filename.Contains("*"))
            {
                var parent = Path.GetDirectoryName(filename);
                return Directory.GetFiles(
                    string.IsNullOrEmpty(parent) ? "." : parent,
                    Path.GetFileName(filename),
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
            }
            else
                return new[] { filename };
        }


        protected IEnumerable<string> ExpandFolderToFiles(string fileOrFolder, string pattern, bool recursive = false)
        {
            if (Directory.Exists(fileOrFolder))
            {
                foreach (var file in Directory.GetFiles(
                    fileOrFolder, 
                    pattern, 
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                {
                    yield return file;
                }
            }
            else
            {
                yield return fileOrFolder;
            }
        }
    }
}
