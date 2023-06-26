using AudioSynchronization;
using CommandLine;
using FunscriptToolbox.Core;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace FunscriptToolbox
{
    internal class Verb
    {
        protected readonly static JsonSerializer Serializer = JsonSerializer
            .Create(new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            });

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
        private readonly string r_ffmpegFolder;

        public FunscriptVault FunscriptVault { get; }

        public int NbErrors { get; private set; }

        public Verb(ILog log, OptionsBase options)
        {
            var appDataFolder = Environment.ExpandEnvironmentVariables($@"%appdata%\{ApplicationName}");

            r_log = log;
            r_options = options;
            r_ffmpegFolder = Path.Combine(appDataFolder, "ffmpeg");
            FFmpeg.SetExecutablesPath(r_ffmpegFolder);

            this.FunscriptVault = new FunscriptVault(Path.Combine(appDataFolder, "Vault"));

            UpdateFfmpeg();
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

        //private void UpdateFfmpegCustom(bool forceUpdate = false)
        //{
        //    var neededFiles = new[] { "ffmpeg.exe", "ffprobe.exe" };
        //    if (forceUpdate || !neededFiles.All(f => File.Exists(Path.Combine(r_ffmpegFolder, f))))
        //    {
        //        if (!forceUpdate)
        //            WriteInfo("ffmpeg/ffprobe missing.");
        //        Directory.CreateDirectory(r_ffmpegFolder);
        //        var url = Settings.Default.FfmpegSourceUrl;
        //        var archiveFilename = Path.Combine(r_ffmpegFolder, url.Substring(url.LastIndexOf("/") + 1));
        //        WriteInfo($"Downloading ffmpeg/ffprobe from '{url}'...");
        //        using (var client = new WebClient())
        //        {
        //            client.DownloadFile(url, archiveFilename);
        //        }
        //        using (var file = SevenZipArchive.Open(archiveFilename))
        //        {
        //            var options = new ExtractionOptions() { 
        //                Overwrite = true, 
        //                PreserveAttributes = true,
        //                PreserveFileTime = true
        //            };
        //            foreach (var neededFile in neededFiles)
        //            {
        //                var entry = file.Entries.FirstOrDefault(e => Path.GetFileName(e.Key).Equals(neededFile, StringComparison.OrdinalIgnoreCase))
        //                    ?? throw new Exception($"Can't find '{neededFile}' in archive '{url}'.");
        //                WriteInfo($"Extracting '{entry.Key}'...");
        //                entry.WriteToFile(
        //                    Path.Combine(r_ffmpegFolder, Path.GetFileName(entry.Key)), 
        //                    options);
        //            }
        //        }
        //    }
        //}

        protected void StartAndHandleFfmpegProgress(IConversion conversion, string outputFile)
        {
            conversion.OnDataReceived += (sender, args) => WriteVerbose($"[ffmpeg]   {args.Data}", isProgress: args.Data.StartsWith("frame="));
            var stopwatch = Stopwatch.StartNew();
            var total = TimeSpan.Zero;
            conversion.OnProgress += (sender, args) =>
            {
                var percent = args.Duration.TotalSeconds / args.TotalLength.TotalSeconds * 100;
                if (percent > 0)
                {
                    var timeLeft = TimeSpan.FromMilliseconds(stopwatch.ElapsedMilliseconds / percent * (100 - percent));
                    if (!r_options.Verbose)
                    {
                        var line = $"[ffmpeg]   [{args.Duration} / {args.TotalLength}] {(int)(Math.Round(percent, 2))}% => elapsed : {stopwatch.Elapsed} left: {timeLeft}";
                        WriteInfo($"{line}{new string(' ', Math.Max(0, Console.WindowWidth - line.Length - 1))}", isProgress: true);
                    }
                    total = args.TotalLength;
                }
            };

            var tempFile = Path.Combine(
                    Path.GetDirectoryName(outputFile) ?? ".",
                    $"{Path.GetFileName(outputFile)}.temp{Path.GetExtension(outputFile)}");

            var conversionCommand = conversion
                .SetOutput(tempFile);
            r_log.Info($"Starting process: ffmpeg {conversionCommand.Build()}");
            var conversionResult = conversionCommand
                .Start();
            ApplicationExitHandler.AddKillProcessAction("ffmpeg", TimeSpan.FromSeconds(15));
            conversionResult.Wait();
            ApplicationExitHandler.RemoveCleanUpAction();

            File.Move(tempFile, outputFile);
            WriteInfo($"[ffmpeg]   Handling a video of {total} took {stopwatch.Elapsed}.");
        }

        private bool m_lastWriteIsProgress = false;

        public void WriteInfo(string message = "", ConsoleColor? color = null, bool isProgress = false)
        {
            if (m_lastWriteIsProgress)
            {
                Console.Write($"{new string(' ', Math.Max(0, Console.WindowWidth - 1))}\r");
            }

            r_log.Info(message);
            if (color != null) Console.ForegroundColor = color.Value;
            if (isProgress)
            {
                Console.Write($"{message}{new string(' ', Math.Max(0, Console.WindowWidth - message.Length - 1))}\r");
            }
            else
            {
                Console.WriteLine(message);
            }
            Console.ResetColor();
            m_lastWriteIsProgress = isProgress;
        }

        public void WriteVerbose(string message = "") => WriteVerbose(message, null, false);

        public void WriteVerbose(string message = "", ConsoleColor? color = null, bool isProgress = false)
        {
            if (m_lastWriteIsProgress)
            {
                Console.Write($"{new string(' ', Math.Max(0, Console.WindowWidth - 1))}\r");
            }

            r_log.Debug(message);
            if (r_options.Verbose)
            {
                if (color != null) Console.ForegroundColor = color.Value;
                if (isProgress)
                {
                    Console.Write($"{message}{new string(' ', Math.Max(0, Console.WindowWidth - message.Length - 1))}\r");
                }
                else
                {
                    Console.WriteLine(message);
                }
                Console.ResetColor();
            }
            m_lastWriteIsProgress = isProgress;
        }

        public void WriteDebug(string message = "")
        {
            r_log.Debug(message);
        }

        public void WriteError(string message = "")
        {
            if (m_lastWriteIsProgress)
            {
                Console.Write($"{new string(' ', Math.Max(0, Console.WindowWidth - 1))}\r");
            }

            r_log.Error(message);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(message);
            Console.ResetColor();
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
                        recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f);
            }
            else
                return new[] { filename };
        }
    }
}
