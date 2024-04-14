using CommandLine;
using log4net;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using FunscriptToolbox.Core;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace FunscriptToolbox.SubtitlesVerbObsolete
{
    class VerbSubtitlesGPT2Srt : Verb
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("subtitles.gpt2srt", aliases: new[] { "sub.gpt2srt" }, HelpText = "Merge srts files (from whisper-translated .srt of a extracted wav from SubtitleEdit)")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = true, HelpText = ".srt files")]
            public IEnumerable<string> Input { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }

            [Option('f', "force", Required = false, HelpText = "Allow to redo existing extraction", Default = false)]
            public bool Force { get; set; }
        }

        private readonly Options r_options;

        public VerbSubtitlesGPT2Srt(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            foreach (var gtpresultFullPath in r_options
                .Input
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive)))
            {
                var inputSrtFullPath = Path.ChangeExtension(gtpresultFullPath, ".srt");
                var inputConfidenceSrtFullPath = Path.ChangeExtension(gtpresultFullPath, ".confidence.srt");
                var outputSrtFullPath = Path.ChangeExtension(inputSrtFullPath, ".en.srt");

                if (!r_options.Force && File.Exists(outputSrtFullPath))
                {
                    WriteInfo($"{gtpresultFullPath}: Skipping because file '{Path.GetFileName(outputSrtFullPath)}' already  (use --force to override).", ConsoleColor.DarkGray);
                    continue;
                }
                if (!File.Exists(gtpresultFullPath))
                {
                    WriteInfo($"{gtpresultFullPath}: Skipping because file '{Path.GetFileName(inputSrtFullPath)}' don't exists.", ConsoleColor.DarkGray);
                    continue;
                }

                var watch = Stopwatch.StartNew();

                WriteInfo($"{gtpresultFullPath}: Loading gptresult file...");

                var gptresults = GetTranslations(gtpresultFullPath)
                    .GroupBy(t => t.Item1)
                    .ToDictionary(t => t.Key, f => f.Select(k => k.Item2).ToArray());

                WriteInfo($"{gtpresultFullPath}: Loading srt file...");
                var transcribedSrt = SubtitleFile.FromSrtFile(inputSrtFullPath);

                WriteInfo($"{gtpresultFullPath}: Loading confidence srt file...");
                var confidenceSrt = SubtitleFile.FromSrtFile(inputConfidenceSrtFullPath);

                WriteInfo($"{gtpresultFullPath}: Creating translated file '{Path.GetFileName(outputSrtFullPath)}'...");
                var oldGptResults = Path.ChangeExtension(gtpresultFullPath, ".old.gptresults");
                Dictionary<string, List<string>> oldTranslations = null;
                if (File.Exists(oldGptResults))
                {
                    oldTranslations = GetTranslationsFromOldResult(oldGptResults, "Can you translate this from Japanese");
                }

                var translatedSrt = new SubtitleFile(outputSrtFullPath);
                foreach (var transcribedSubtitle in transcribedSrt.Subtitles)
                {
                    var confidenceSubtitle = confidenceSrt.Subtitles.FirstOrDefault(f => f.StartTime == transcribedSubtitle.StartTime && f.EndTime == transcribedSubtitle.EndTime);
                    var confidenceLines = confidenceSubtitle == null ? Array.Empty<string>() : confidenceSubtitle.Lines;

                    var lines = new List<string>();
                    if (gptresults.TryGetValue(transcribedSubtitle.Number.Value, out var translationChoices))
                    {
                        var uniquesChoice = translationChoices
                            .Select(f => RemoveQuotes(f))
                            .GroupBy(f => string.Join("**", f))
                            .Select(f => f.FirstOrDefault())
                            .ToArray();

                        foreach (var choice in uniquesChoice)
                        {
                            if (lines.Count > 0)
                            {
                                lines.Add("* ** OR ***");
                            }
                            lines.AddRange(choice);
                        }
                        lines.AddRange(confidenceLines);

                        if (oldTranslations != null)
                        {
                            foreach (var line in transcribedSubtitle.Lines)
                            {
                                if (oldTranslations.TryGetValue(line, out var translations))
                                {
                                    lines.AddRange(RemoveQuotes(translations.ToArray()).Distinct());
                                }
                            }
                        }
                    }
                    else
                    {
                        translatedSrt.Subtitles.Add(
                            new Subtitle(
                                transcribedSubtitle.StartTime,
                                transcribedSubtitle.EndTime,
                                "[No translation Found]"));

                        lines.AddRange(transcribedSubtitle.Lines);
                    }
                    translatedSrt.Subtitles.Add(
                        new Subtitle(
                            transcribedSubtitle.StartTime,
                            transcribedSubtitle.EndTime,
                            lines.ToArray()));
                }

                foreach (var subtitle in confidenceSrt.Subtitles.Where(f => f.Lines.First().Contains("[No Subtitle found in this range]")))
                {
                    translatedSrt.Subtitles.Add(subtitle);
                }    

                translatedSrt.SaveSrt();

                WriteInfo($"{gtpresultFullPath}: Finished in {watch.Elapsed}.");
                WriteInfo();
            }
            return base.NbErrors;
        }

        private string[] RemoveQuotes(string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length >= 2 && line.StartsWith("\"") && line.EndsWith("\""))
                {
                    lines[i] = line.Substring(1, line.Length - 2);
                }
            }
            return lines;
        }

        public static IEnumerable<TSource> DistinctBy<TSource, TKey>(IEnumerable<TSource> source,
            Func<TSource, TKey> keySelector, IEqualityComparer<TKey> comparer)
        {
            HashSet<TKey> knownKeys = new HashSet<TKey>(comparer);
            foreach (TSource element in source)
            {
                if (knownKeys.Add(keySelector(element)))
                {
                    yield return element;
                }
            }
        }

        private IEnumerable<Tuple<int, string[]>> GetTranslations(string gtpresultFullPath)
        {
            string pattern = @"^\s*\[(?<Number>\d+)(-R\]|\]\s*-R)\s*(?<Line>.*)$";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);

            int? currentNumber = null;
            var currentLines = new List<string>();
            foreach (var line in File.ReadLines(gtpresultFullPath))
            {
                var match = regex.Match(line);
                if (match.Success)
                {
                    if (currentNumber != null)
                    {
                        yield return new Tuple<int, string[]>(currentNumber.Value, currentLines.ToArray());
                    }
                    currentNumber = int.Parse(match.Groups["Number"].Value);
                    currentLines.Clear();

                    if (match.Groups["Line"].Success && !string.IsNullOrWhiteSpace(match.Groups["Line"].Value))
                    {
                        currentLines.Add(match.Groups["Line"].Value);
                    }
                }
                else if (string.IsNullOrWhiteSpace(line))
                {
                    if (currentNumber != null)
                    {
                        yield return new Tuple<int, string[]>(currentNumber.Value, currentLines.ToArray());
                    }
                    currentNumber = null;
                    currentLines.Clear();
                }
                else if (currentNumber != null)
                {
                    currentLines.Add(line);
                }
                else
                {
                    // Do Nothing
                }
            }
        }

        private static Dictionary<string, List<string>> GetTranslationsFromOldResult(string filename, string lineThatReset)
        {
            var lines = File.ReadAllLines(filename);
            var translations = new Dictionary<string, List<string>>();

            var currentQuestions = new Dictionary<int, string>();
            var indexLine = 0;
            while (indexLine < lines.Length)
            {
                var line = lines[indexLine++];
                if (line.Contains(lineThatReset))
                {
                    currentQuestions.Clear();
                }

                string pattern = @"^\s*\[(?<Number>\d+)\](?<Text>.*)$";
                var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                var match = regex.Match(line);
                if (match.Success)
                {
                    var number = int.Parse(match.Groups["Number"].Value);
                    var text = match.Groups["Text"].Value;
                    if (text.StartsWith("-R"))
                    {
                        text = text.Substring(2);
                        if (text.Trim().Length == 0)
                        {
                            text = lines[indexLine++];
                        }
                        if (currentQuestions.TryGetValue(number, out string japText))
                        {
                            if (!translations.TryGetValue(japText, out var list))
                            {
                                list = new List<string>();
                                translations[japText] = list;
                            }
                            list.Add(text);
                        }
                    }
                    else
                    {
                        if (text.Trim().Length == 0)
                        {
                            text = lines[indexLine++];
                        }
                        currentQuestions[number] = text;
                    }
                }
            }
            return translations;
        }

    }
}