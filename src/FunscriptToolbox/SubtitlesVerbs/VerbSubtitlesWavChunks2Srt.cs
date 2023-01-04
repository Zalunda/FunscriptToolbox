using CommandLine;
using FunscriptToolbox.Core;
using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerb
{

    class VerbSubtitlesWavChunks2Srt : VerbSubtitles
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("subtitles.wavchunks2srt", aliases: new[] { "sub.wavs2srt" }, HelpText = "Merge .srt file transcribed by whisper to a single srt")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = true, HelpText = "temp.vad.srt files")]
            public IEnumerable<string> Input { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }

            [Option('f', "force", Required = false, HelpText = "Allow to force the execution", Default = false)]
            public bool Force { get; set; }

            [Option('l', "lang", Required = false, HelpText = "Transcribed langage, will be used for the .<lang>.srt", Default = "jp")]
            public string TranscribedLanguage { get; set; }

            [Option('p', "name pattern", HelpText = "Pattern to extract start and times from the name of the file", Default = @"__(?<StartTime>\d+)_(?<Duration>\d+)__")]
            public string NamePattern { get; set; }
        }

        private readonly Options r_options;
        private readonly Regex r_regex;

        public VerbSubtitlesWavChunks2Srt(Options options)
            : base(rs_log, options)
        {
            r_options = options;
            r_regex = new Regex(options.NamePattern, RegexOptions.IgnoreCase);
        }

        public int Execute()
        {
            foreach (var srtFullpath in r_options
                .Input
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive))
                .Distinct()
                .OrderBy(f => f))
            {
                try
                {
                    var parentFolder = Path.GetDirectoryName(srtFullpath) ?? ".";
                    var baseOutput = Path.Combine(parentFolder, Path.GetFileNameWithoutExtension(srtFullpath));
                    var fileId = Path.GetFileName(srtFullpath).GetHashCode().ToString("X8");
                    var chunksFolder = $"{baseOutput}_wav_chunks_{fileId}";
                    var outputSrtFullpath = baseOutput
                        .Replace(".temp", string.Empty)
                        .Replace(".vad", string.Empty) 
                        + $".{r_options.TranscribedLanguage}.srt";

                    if (!r_options.Force && File.Exists(outputSrtFullpath))
                    {
                        WriteInfo($"{srtFullpath}: Skipping because file '{Path.GetFileName(outputSrtFullpath)}' already  (use --force to override).");
                        continue;
                    }

                    if (!Directory.Exists(chunksFolder))
                    {
                        WriteInfo($"{srtFullpath}: Cannot create transcribed .srt because folder '{chunksFolder}' doesn't exists.");
                        continue;
                    }

                    var chunkFilepaths = Directory.GetFiles(chunksFolder, "*" + SubtitleFile.SrtExtension);
                    if (chunkFilepaths.Length == 0)
                    {
                        WriteInfo($"{srtFullpath}: Cannot create transcribed .srt because there is no .srt files in the folder '{chunksFolder}'.");
                        continue;
                    }

                    var watch = Stopwatch.StartNew();

                    var srtFile = SubtitleFile.FromSrtFile(srtFullpath);
                    var outputSrt = new SubtitleFile(outputSrtFullpath);

                    foreach (var chunkFilepath in chunkFilepaths)
                    {
                        var match = r_regex.Match(Path.GetFileName(chunkFilepath));
                        if (match.Success)
                        {
                            var fileStartTime = TimeSpan.FromMilliseconds(GetNumberFromMatch(match, "StartTime"));
                            var fileEndTime = fileStartTime
                                + TimeSpan.FromMilliseconds(GetNumberFromMatch(match, "Duration"));

                            var inputSrt = SubtitleFile.FromSrtFile(chunkFilepath);
                            if (inputSrt.Subtitles.Count == 0)
                            {
                                outputSrt.Subtitles.Add(new Subtitle(fileStartTime, fileEndTime, "."));
                            }
                            else
                            {
                                for (int i = 0; i < inputSrt.Subtitles.Count; i++)
                                {
                                    var subtitle = inputSrt.Subtitles[i];
                                    var newStartTime = (i == 0)
                                        ? fileStartTime
                                        : fileStartTime + subtitle.StartTime;
                                    var newEndTime = (i == inputSrt.Subtitles.Count - 1)
                                        ? fileEndTime
                                        : fileStartTime + subtitle.EndTime;
                                    outputSrt.Subtitles.Add(
                                        new Subtitle(
                                            newStartTime,
                                            newEndTime,
                                            subtitle.Lines));
                                }
                            }
                        }
                        else
                        {
                            throw new ArgumentException($"Cannot extract start and end time from the filename '{chunkFilepath}', it need to match this pattern: {r_options.NamePattern}");
                        }
                    }

                    outputSrt.SaveSrt();

                    WriteInfo($"{srtFullpath}: Finished in {watch.Elapsed}.");
                }
                catch (Exception ex)
                {
                    WriteError($"{srtFullpath}: Exception occured: {ex}");
                }
            }

            return base.NbErrors;
        }

        private int GetNumberFromMatch(Match match, string groupName)
        {
            var stringValue = match.Groups[groupName].Value;
            if (int.TryParse(stringValue, out var intValue))
            {
                return intValue;
            }
            else
            {
                throw new ArgumentException($"Cannot convert '{stringValue}' into an integer.");
            }
        }
    }
}
