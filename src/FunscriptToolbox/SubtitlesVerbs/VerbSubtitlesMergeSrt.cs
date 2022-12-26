//using CommandLine;
//using CommandLine.Text;
//using log4net;
//using System.Collections.Generic;
//using System.IO;
//using System.Linq;
//using System;
//using System.Text.RegularExpressions;
//using FunscriptToolbox;
//using FunscriptToolbox.Core;

//namespace FunscriptToolbox.SubtitlesVerb
//{
//    class VerbSubtitlesMergeSrt : Verb
//    {
//        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

//        [Verb("subtitles.mergesrt", aliases: new[] { "sub.mergesrt" }, HelpText = "Merge srts files (from whisper-translated .srt of a extracted wav from SubtitleEdit)")]
//        public class Options : OptionsBase
//        {
//            [Value(0, MetaName = "files", Required = true, HelpText = ".srt files or a folder")]
//            public IEnumerable<string> Input { get; set; }

//            [Option('o', "output", Required = true, HelpText = "output .srt file")]
//            public string Output { get; set; }

//            [Option('p', "name pattern", HelpText = "Pattern to extract start and times from the name of the file", Default = @"^(\d+_)?\d+_(?<StartHours>\d+)_(?<StartMinutes>\d+)_(?<StartSeconds>\d+)_(?<StartMilliseconds>\d+)__(?<EndHours>\d+)_(?<EndMinutes>\d+)_(?<EndSeconds>\d+)_(?<EndMilliseconds>\d{3})")]
//            public string NamePattern { get; set; }

//            [Usage(ApplicationAlias = Verb.ApplicationName)]
//            public static IEnumerable<Example> Examples
//            {
//                get
//                {
//                    yield break;
//                }
//            }
//        }

//        private readonly Options r_options;
//        private readonly Regex r_regex;

//        public VerbSubtitlesMergeSrt(Options options)
//            : base(rs_log, options)
//        {
//            r_options = options;
//            r_regex = new Regex(r_options.NamePattern, RegexOptions.IgnoreCase);
//        }

//        public int Execute()
//        {
//            var outputFile = new SubtitleFile();
//            foreach (var filepath in r_options
//                .Input
//                .SelectMany(fileOrFolder => ExpandFolderToFiles(fileOrFolder, $"*{SubtitleFile.SrtExtension}"))
//                .SelectMany(file => HandleStarAndRecusivity(file)))
//            {
//                var filename = Path.GetFileName(filepath);
//                if (string.Equals(Path.GetExtension(filepath), SubtitleFile.SrtExtension, StringComparison.OrdinalIgnoreCase))
//                {
//                    var match = r_regex.Match(Path.GetFileName(filepath));
//                    if (match.Success)
//                    {
//                        var fileStartTime = new TimeSpan(
//                            0,
//                            GetNumberFromMatch(match, "StartHours"),
//                            GetNumberFromMatch(match, "StartMinutes"),
//                            GetNumberFromMatch(match, "StartSeconds"),
//                            GetNumberFromMatch(match, "StartMilliseconds"));
//                        var fileEndTime = new TimeSpan(
//                            0,
//                            GetNumberFromMatch(match, "EndHours"),
//                            GetNumberFromMatch(match, "EndMinutes"),
//                            GetNumberFromMatch(match, "EndSeconds"),
//                            GetNumberFromMatch(match, "EndMilliseconds"));

//                        var inputSrt = SubtitleFile.FromSrtFile(filepath);
//                        if (inputSrt.Subtitles.Count == 0)
//                        {
//                            WriteInfo($"{filename}: Loaded file with no subtitle, creating a dummy entry.", ConsoleColor.Yellow);
//                            outputFile.Subtitles.Add(new Subtitle(fileStartTime, fileEndTime, "."));
//                        }
//                        else
//                        {
//                            for (int i = 0; i < inputSrt.Subtitles.Count; i++)
//                            {
//                                var subtitle = inputSrt.Subtitles[i];
//                                var newStartTime = (i == 0) 
//                                    ? fileStartTime 
//                                    : fileStartTime + subtitle.StartTime;
//                                var newEndTime = (i == inputSrt.Subtitles.Count - 1) 
//                                    ? fileEndTime 
//                                    : fileStartTime + subtitle.EndTime;
//                                outputFile.Subtitles.Add(
//                                    new Subtitle(
//                                        newStartTime,
//                                        newEndTime,
//                                        subtitle.Lines));
//                            }
//                        }
//                    }
//                    else
//                    {
//                        throw new ArgumentException($"Cannot extract start and end time from the filename '{filename}', it need to match this pattern: {r_options.NamePattern}");
//                    }
//                }
//                else 
//                {
//                    WriteInfo($"{filename}: Ignoring. Only {SubtitleFile.SrtExtension} files are supported.", ConsoleColor.Yellow);
//                }
//            }

//            WriteInfo($"{r_options.Output}: Saving the final file, containing {outputFile.Subtitles.Count} subtitles");
//            outputFile.SaveSrt(r_options.Output);
//            return base.NbErrors;
//        }

//        private int GetNumberFromMatch(Match match, string groupName)
//        {
//            var stringValue = match.Groups[groupName].Value;
//            if (int.TryParse(stringValue, out var intValue))
//            {
//                return intValue;
//            }
//            else
//            {
//                throw new ArgumentException($"Cannot convert '{stringValue}' into an integer.");
//            }
//        }
//    }
//}
