using AudioSynchronization;
using CommandLine;
using FunscriptToolbox.Core;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

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
        
        public FunscriptVault FunscriptVault { get; }

        public int NbErrors { get; private set; }

        public Verb(ILog log, OptionsBase options)
        {
            var appDataFolder = Environment.ExpandEnvironmentVariables($@"%appdata%\{ApplicationName}");

            r_log = log;
            r_options = options;
            FFmpeg.SetExecutablesPath(Path.Combine(appDataFolder, "ffmpeg"));

            this.FunscriptVault = new FunscriptVault(Path.Combine(appDataFolder, "Vault"));
        }

        protected string GetApplicationFolder(string relativePath = null)
        {
            var applicationFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return relativePath == null
                ? applicationFolder
                : Path.Combine(applicationFolder, relativePath);
        }

        protected FunscriptAudioSignature Convert(AudioSignature signature)
        {
            return new FunscriptAudioSignature(signature.NbSamplesPerSecond, signature.CompressedSamples);
        }
        protected AudioSignature Convert(FunscriptAudioSignature signature)
        {
            return new AudioSignature(signature.NbSamplesPerSecond, signature.CompressedSamples);
        }

        protected void UpdateFfmpeg()
        {
            UpdateFfmpegAsync().GetAwaiter().GetResult();
        }

        protected async Task UpdateFfmpegAsync()
        {
            var oldCurrentDirectory = Environment.CurrentDirectory;
            try
            {
                Directory.CreateDirectory(FFmpeg.ExecutablesPath);
                Environment.CurrentDirectory = FFmpeg.ExecutablesPath;
                await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official);
            }
            finally
            {
                Environment.CurrentDirectory = oldCurrentDirectory;
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
