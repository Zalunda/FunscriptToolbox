using CommandLine;
using log4net;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using FunscriptToolbox.Core;
using System.Diagnostics;

namespace FunscriptToolbox.SubtitlesVerb
{
    class VerbSubtitlesSrt2GPT : Verb
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("subtitles.srt2gpt", aliases: new[] { "sub.srt2gpt" }, HelpText = "Create ChatGPT prompts for translations")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = true, HelpText = ".srt files")]
            public IEnumerable<string> Input { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }

            [Option('f', "force", Required = false, HelpText = "Allow to force the execution", Default = false)]
            public bool Force { get; set; }
        }

        private readonly Options r_options;

        public VerbSubtitlesSrt2GPT(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            foreach (var inputSrtFullpath in r_options
                .Input
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive)))
            {
                try
                {
                    var outputGptFullpath = Path.ChangeExtension(inputSrtFullpath, ".gptprompts");

                    if (!r_options.Force && File.Exists(outputGptFullpath))
                    {
                        WriteInfo($"{inputSrtFullpath}: Skipping because file '{Path.GetFileName(outputGptFullpath)}' already exists.  (use --force to override)");
                        continue;
                    }

                    var watch = Stopwatch.StartNew();

                    WriteInfo($"{inputSrtFullpath}: Loading input srt...");
                    var inputSrtFile = SubtitleFile.FromSrtFile(inputSrtFullpath);

                    WriteInfo($"{inputSrtFullpath}: Creating prompts for chatgpt...");
                    using (var writer = File.CreateText(outputGptFullpath))
                    {
                        var index = 1;
                        foreach (var subtitle in inputSrtFile.Subtitles)
                        {
                            if (index == 1)
                            {
                                writer.WriteLine("Can you translate this from japanese to english, considering that it only include what a women say to a men.");
                                writer.WriteLine("It doesn't include what the men is saying.");
                                writer.WriteLine("Please include the separator but add \"-R\" after the number that I included so that I know which line is translated to which text.");
                                writer.WriteLine();
                                writer.WriteLine("For example:");
                                writer.WriteLine();
                                writer.WriteLine("[1234]");
                                writer.WriteLine("日本語のサンプルテキスト");
                                writer.WriteLine();
                                writer.WriteLine("Should return:");
                                writer.WriteLine();
                                writer.WriteLine("[1234-R]");
                                writer.WriteLine("Sample Text In Japanese");
                                writer.WriteLine();
                                writer.WriteLine("Here is the texs to translate:");

                                //writer.WriteLine("Can you translate this from japanese to english, considering that it's what a women say to a men.");
                                //writer.WriteLine("It doesn't include what the men is saying.");
                                //writer.WriteLine("Please include the separator but add \"-R\" after the number that I included so that I know which line is translated to which text.");
                                writer.WriteLine();
                            }

                            if (subtitle.Lines.Any(f => f.Contains("[")))
                            {
                                // Skipping 'comment'
                            }
                            else
                            {
                                writer.WriteLine($"[{subtitle.Number:D4}]");
                                foreach (var line in subtitle.Lines)
                                {
                                    writer.WriteLine(line);
                                }

                                if (index % 30 == 0)
                                {
                                    writer.WriteLine();
                                    writer.WriteLine();
                                }
                                index++;
                            }
                        }
                    }

                    WriteInfo($"{inputSrtFullpath}: Finished in {watch.Elapsed}.");
                    WriteInfo();
                }
                catch (Exception ex)
                {
                    WriteError($"{inputSrtFullpath}: Exception occured: {ex}");
                    WriteInfo();
                }
            }
            return base.NbErrors;
        }
    }
}