using CommandLine;
using log4net;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using FunscriptToolbox.Core;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace FunscriptToolbox.SubtitlesVerb
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
                var transcribedSrtFullPath = Path.ChangeExtension(gtpresultFullPath, ".srt");
                var translatedSrtFullPath = Path.ChangeExtension(transcribedSrtFullPath, ".en.srt");

                if (!r_options.Force && File.Exists(translatedSrtFullPath))
                {
                    WriteInfo($"{gtpresultFullPath}: Skipping because file '{Path.GetFileName(translatedSrtFullPath)}' already  (use --force to override).");
                    continue;
                }
                if (!File.Exists(gtpresultFullPath))
                {
                    WriteInfo($"{gtpresultFullPath}: Skipping because file '{Path.GetFileName(transcribedSrtFullPath)}' don't exists.");
                    continue;
                }

                var watch = Stopwatch.StartNew();

                WriteInfo($"{gtpresultFullPath}: Loading gptresult file...");

                var gptresults = GetTranslations(gtpresultFullPath)
                    .GroupBy(t => t.Item1)
                    .ToDictionary(t => t.Key, f => f.Select(k => k.Item2).ToArray());

                WriteInfo($"{gtpresultFullPath}: Loading srt file...");
                var transcribedSrt = SubtitleFile.FromSrtFile(transcribedSrtFullPath);

                WriteInfo($"{gtpresultFullPath}: Creating translated file '{Path.GetFileName(translatedSrtFullPath)}'...");
                var translatedSrt = new SubtitleFile(translatedSrtFullPath);
                foreach (var transcribedSubtitle in transcribedSrt.Subtitles)
                {
                    if (gptresults.TryGetValue(transcribedSubtitle.Number.Value, out var translationChoices))
                    {
                        var uniquesChoice = translationChoices
                            .GroupBy(f => string.Join("**", f))
                            .Select(f => f.FirstOrDefault())
                            .ToArray();

                        if (uniquesChoice.Length == 1)
                        {
                            translatedSrt.Subtitles.Add(
                                new Subtitle(
                                    transcribedSubtitle.StartTime,
                                    transcribedSubtitle.EndTime,
                                    translationChoices.First()));
                        }
                        else
                        {
                            var lines = new List<string>();
                            foreach (var choice in uniquesChoice)
                            {
                                if (lines.Count > 0)
                                {
                                    lines.Add("*** OR ***");
                                }
                                lines.AddRange(choice);
                            }
                            translatedSrt.Subtitles.Add(
                                new Subtitle(
                                    transcribedSubtitle.StartTime,
                                    transcribedSubtitle.EndTime,
                                    lines.ToArray()));
                        }
                    }
                    else
                    {
                        translatedSrt.Subtitles.Add(
                            new Subtitle(
                                transcribedSubtitle.StartTime,
                                transcribedSubtitle.EndTime,
                                "[No translation Found]"));
                        translatedSrt.Subtitles.Add(
                            transcribedSubtitle);
                    }
                }
                translatedSrt.SaveSrt();

                WriteInfo($"{gtpresultFullPath}: Finished in {watch.Elapsed}.");
                WriteInfo();
            }
            return base.NbErrors;
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
    }
}