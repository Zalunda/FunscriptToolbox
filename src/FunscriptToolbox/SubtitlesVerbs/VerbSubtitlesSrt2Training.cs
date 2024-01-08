using CommandLine;
using log4net;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using FunscriptToolbox.Core;
using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace FunscriptToolbox.SubtitlesVerb
{
    class VerbSubtitlesSrt2Training : Verb
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("subtitles.srt2train", aliases: new[] { "sub.srt2train" }, HelpText = "Create ChatGPT prompts for translations")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = true, HelpText = ".srt files")]
            public IEnumerable<string> Input { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }

            [Option('f', "force", Required = false, HelpText = "Allow to force the execution", Default = false)]
            public bool Force { get; set; }

            [Option('o', "output", Required = false, HelpText = "Suffix for the files produced", Default = "")]
            public string Output { get; set; }

            public int ContextBefore { get; internal set; } = 5;
            public int ContextAfter { get; internal set; } = 3;
            public int NbSubtitlesToTranslate { get; internal set; } = 1;
        }

        private readonly Options r_options;

        public VerbSubtitlesSrt2Training(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            var outputTrainingDataFile = r_options.Output;

            if (!r_options.Force && File.Exists(outputTrainingDataFile))
            {
                WriteInfo($"{outputTrainingDataFile}: Skipping because file '{Path.GetFileName(outputTrainingDataFile)}' already exists.  (use --force to override)", ConsoleColor.DarkGray);
                return 0;
            }

            var trainingData = new List<AlpacaData>();

            foreach (var inputContextFullpath in r_options
                .Input
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive)))
            {
                var baseName = Path.GetFileNameWithoutExtension(inputContextFullpath);
                var inputTranscribedFile = Path.ChangeExtension(inputContextFullpath, ".jp.srt");
                var inputTranslatedFile = Path.ChangeExtension(inputContextFullpath, ".en.srt");

                try
                {
                    var watch = Stopwatch.StartNew();

                    WriteInfo($"{baseName}: Loading transcribed srt...");
                    var transcribedSrtFile = SubtitleFile.FromSrtFile(inputTranscribedFile);

                    WriteInfo($"{baseName}: Loading translated srt...");
                    var translatedSrtFile = SubtitleFile.FromSrtFile(inputTranslatedFile);

                    for (int i = 0; i < transcribedSrtFile.Subtitles.Count;)
                    {
                        var transcribed = transcribedSrtFile.Subtitles[i];

                        var input = new List<string>();
                        var output = new List<string>();
                        for (int k = 1; k <= r_options.ContextBefore; k++)
                        {
                            if (i - k >= 0)
                                input.AddRange(transcribedSrtFile.Subtitles[i - k].Lines);
                        }
                        output.Add("Here are the translation for the specified lines:");
                        for (int k = 0; k < r_options.NbSubtitlesToTranslate; k++)
                        {
                            if (i < transcribedSrtFile.Subtitles.Count)
                            {
                                var subtitle = transcribedSrtFile.Subtitles[i++];
                                var translateds = translatedSrtFile.Subtitles.Where(s => s.StartTime == subtitle.StartTime).ToArray();

                                if (translateds.Length >= 1)
                                {
                                    // if Length > 1 warning ???

                                    input.Add($"***** " + string.Join(" ", subtitle.Lines));

                                    output.Add(string.Join(" ", subtitle.Lines) + " =>");
                                    output.Add(string.Join(" ", translateds.First().Lines));
                                    output.Add("");
                                }
                            }
                        }
                        for (int k = 0; k < r_options.ContextAfter; k++)
                        {
                            var index = i + k;
                            if (index < transcribedSrtFile.Subtitles.Count)
                            {
                                input.AddRange(transcribedSrtFile.Subtitles[index].Lines);
                            }
                        }

                        trainingData.Add(new AlpacaData
                        {
                            Instruction = "Translate JAV lines to english (prefixed with '*****').",
                            Input = string.Join("\n", input),
                            Output = string.Join("\n", output),
                        });
                    }

                    using (var writer = File.CreateText(outputTrainingDataFile))
                    {
                        var serializer = JsonSerializer
                            .Create(new JsonSerializerSettings
                            {
                                Formatting = Formatting.Indented,
                                ContractResolver = new CamelCasePropertyNamesContractResolver()
                            });
                        serializer.Serialize(writer, trainingData);
                    }
                }
                catch (Exception ex)
                {
                    WriteError($"{baseName}: Exception occured: {ex}");
                    WriteInfo();
                }
            }
            return base.NbErrors;
        }
    }
}