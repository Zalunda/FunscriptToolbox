using AudioSynchronization;
using CommandLine;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
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

        public FunscriptVault FunscriptVault { get; }
        public AudioTracksAnalyzer AudioAnalyzer { get; }
        public int NbErrors { get; private set; }

        public Verb(ILog log, OptionsBase options)
        {
            r_log = log;
            r_options = options;

            var appDataFolder = Environment.ExpandEnvironmentVariables($@"%appdata%\{ApplicationName}");
            this.FunscriptVault = new FunscriptVault(Path.Combine(appDataFolder, "Vault"));
            this.AudioAnalyzer = new AudioTracksAnalyzer();
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
            Console.Error.WriteLine(message);
            this.NbErrors++;
        }

        protected static string FormatTimeSpan(TimeSpan value)
        {
            return Regex.Replace(value.ToString(), @"\d{4}$", "");
                
        }

        protected IEnumerable<string> HandleStarAndRecusivity(string filename, bool recursive)
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
    }
}
