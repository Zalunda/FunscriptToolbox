using CommandLine;
using System;
using System.Collections.Generic;
using System.IO;

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

        private readonly OptionsBase r_options;
        public int NbErrors { get; private set; }

        public Verb(OptionsBase options)
        {
            r_options = options;
        }

        public void WriteInfo(string message = "")
        {
            Console.WriteLine(message);
        }

        public void WriteVerbose(string message = "")
        {
            if (r_options.Verbose)
            {
                Console.WriteLine(message);
            }                
        }

        public void WriteError(string message = "")
        {
            Console.Error.WriteLine(message);
            this.NbErrors++;
        }

        protected string FormatTimeSpan(TimeSpan value)
        {
            return value.ToString(@"hh\:mm\:ss\.fff");
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
