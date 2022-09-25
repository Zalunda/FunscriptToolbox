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
        }

        private readonly OptionsBase r_options;

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
