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
                    var outputGptPromptsFullpath = Path.ChangeExtension(inputSrtFullpath, ".gptprompts");
                    var outputGptResultsFullpath = Path.ChangeExtension(inputSrtFullpath, ".gptresults");

                    if (!r_options.Force && File.Exists(outputGptPromptsFullpath))
                    {
                        WriteInfo($"{inputSrtFullpath}: Skipping because file '{Path.GetFileName(outputGptPromptsFullpath)}' already exists.  (use --force to override)", ConsoleColor.DarkGray);
                        continue;
                    }

                    var watch = Stopwatch.StartNew();

                    WriteInfo($"{inputSrtFullpath}: Loading input srt...");
                    var inputSrtFile = SubtitleFile.FromSrtFile(inputSrtFullpath);

                    WriteInfo($"{inputSrtFullpath}: Creating prompts for chatgpt...");
                    WriteGtpPromptFile(outputGptPromptsFullpath, inputSrtFile);

                    var lines = new List<string>();
                    foreach (var translationService in new[] { "google", "yandex", "deepL", "microsoft" })
                    {
                        var inputServiceEnSrtFullPath = Path.ChangeExtension(inputSrtFullpath, $".{translationService}.en.srt");
                        if (File.Exists(inputServiceEnSrtFullPath))
                        {
                            WriteInfo($"{inputSrtFullpath}: Loading translations from {translationService} found in '{Path.GetFileName(inputServiceEnSrtFullPath)}'...");
                            lines.AddRange(ExtractTranslationsFromSrt(SubtitleFile.FromSrtFile(inputServiceEnSrtFullPath)));
                        }
                        else
                        {
                            WriteInfo($"{inputSrtFullpath}: Skipping translations from {translationService}, no file found...", ConsoleColor.DarkGray);
                        }
                    }

                    WriteInfo($"{inputSrtFullpath}: Creating .gptresults file...");
                    File.WriteAllLines(outputGptResultsFullpath, lines);

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

        private static IEnumerable<string> ExtractTranslationsFromSrt(SubtitleFile srtFile)
        {
            foreach (var subtitle in srtFile.Subtitles)
            {
                if (subtitle.Lines.Any(f => f.Contains("[")))
                {
                    // Skipping 'comment'
                }
                else
                {
                    yield return $"[{subtitle.Number:D4}]-R";
                    foreach (var line in subtitle.Lines)
                    {
                        yield return line;
                    }
                    yield return string.Empty;
                }
            }
        }

        private static void WriteGtpPromptFile(string outputFullpath, SubtitleFile srtFile)
        {
            using (var writer = File.CreateText(outputFullpath))
            {
                var index = 1;
                foreach (var subtitle in srtFile.Subtitles)
                {
                    if (index == 1)
                    {
                        writer.WriteLine("Can you translate this from Japanese to English, considering that it only includes what a woman says to a man.");
                        writer.WriteLine("Each bloc of lines starts with a label in this format: \"[0000]\". ");
                        writer.WriteLine("The label is not part of the text to translate. ");
                        writer.WriteLine("You should start your translation with the same label, followed by \"-R\" (ex. \"[0000]-R\"). ");
                        writer.WriteLine("Each line needs to be translated by itself.");
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
        }
    }
}