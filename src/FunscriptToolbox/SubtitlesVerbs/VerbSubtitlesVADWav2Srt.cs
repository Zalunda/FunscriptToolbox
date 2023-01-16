using AudioSynchronization;
using CommandLine;
using FunscriptToolbox.Core;
using FuzzySharp;
using log4net;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerb
{
    class VerbSubtitlesVADWav2Srt : Verb
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("subtitles.vadwav2srt", aliases: new[] { "sub.vadwavsrt" }, HelpText = "Created .srt from the transcribed result from whisper")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = true, HelpText = ".wav")]
            public IEnumerable<string> Input { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }

            [Option('f', "force", Required = false, HelpText = "Allow to force the execution", Default = false)]
            public bool Force { get; set; }

            [Option('s', "suffix", Required = false, HelpText = "Suffix for the files produced", Default = "")]
            public string Suffix { get; set; }
        }

        private readonly Options r_options;

        public VerbSubtitlesVADWav2Srt(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            foreach (var inputWavFullpath in r_options
                .Input
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive)))
            {
                try
                {
                    var inputSrtFullpath = Path.ChangeExtension(inputWavFullpath, ".srt");
                    var inputOffsetFullpath = Path.ChangeExtension(inputWavFullpath, ".offset");
                    var inputChunksSrtFullpath = Path.ChangeExtension(inputWavFullpath, ".chunks.srt");
                    var outputSrtFullpath = Path.ChangeExtension(inputWavFullpath, $"{r_options.Suffix}.srt");
                    var outputConfidenceSrtFullpath = Path.ChangeExtension(inputWavFullpath, $"{r_options.Suffix}.confidence.srt");
                    var verboseSrtFullpath = Path.ChangeExtension(inputWavFullpath, $"{r_options.Suffix}.verbose.txt");

                    if (!r_options.Force && File.Exists(outputSrtFullpath))
                    {
                        WriteInfo($"{inputWavFullpath}: Skipping because '{Path.GetFileName(outputSrtFullpath)}' already exists (use --force to override).", ConsoleColor.DarkGray);
                        continue;
                    }
                    if (!File.Exists(inputSrtFullpath) || !File.Exists(inputOffsetFullpath) || !File.Exists(inputChunksSrtFullpath))
                    {
                        WriteInfo($"{inputWavFullpath}: Skipping because can't find a file named '{Path.GetFileName(inputSrtFullpath)}' and '{Path.GetFileName(inputOffsetFullpath)}' and '{Path.GetFileName(inputChunksSrtFullpath)}'.", ConsoleColor.DarkGray);
                        continue;
                    }

                    var watch = Stopwatch.StartNew();

                    WriteInfo($"{inputWavFullpath}: Loading input srt...");
                    var whisperSrtFile = SubtitleFile.FromSrtFile(inputSrtFullpath);

                    WriteInfo($"{inputWavFullpath}: Loading chunks srt (if present)...");
                    var chunksSrtFile = File.Exists(inputChunksSrtFullpath)
                        ? SubtitleFile.FromSrtFile(inputChunksSrtFullpath)
                        : new SubtitleFile();

                    CreateFiles(
                        string.Empty,
                        inputWavFullpath,
                        inputOffsetFullpath,
                        whisperSrtFile,
                        chunksSrtFile,
                        outputSrtFullpath,
                        outputConfidenceSrtFullpath,
                        verboseSrtFullpath,
                        TimeSpan.FromSeconds(2),
                        50);

                    WriteInfo($"{inputWavFullpath}: Finished in {watch.Elapsed}.");
                    WriteInfo();
                }
                catch (Exception ex)
                {
                    WriteError($"{inputWavFullpath}: Exception occured: {ex}");
                    WriteInfo();
                }
            }

            return base.NbErrors;
        }

        private void CreateFiles(
            string suffix,
            string inputWavFullpath,
            string inputOffsetFullpath,
            SubtitleFile whisperSrtFile,
            SubtitleFile chunksSrtFile,
            string outputSrtFullpath,
            string outputConfidenceSrtFullpath,
            string verboseSrtFullpath,
            TimeSpan distanceMax,
            int minimumConfidence)
        {
            WriteInfo($"{inputWavFullpath}: Loading input offset...");
            WorkingOffset[] whisperOffsets;
            using (var reader = File.OpenText(inputOffsetFullpath))
            using (var jsonReader = new JsonTextReader(reader))
            {
                whisperOffsets = Serializer
                    .Deserialize<AudioOffset[]>(jsonReader)
                    .Select(wo => new WorkingOffset(wo, chunksSrtFile.Subtitles))
                    .ToArray();
            }

            WriteInfo($"{inputWavFullpath}: Applying offsets...");
            using (var writer = r_options.Verbose ? File.CreateText(verboseSrtFullpath + suffix) : new StreamWriter(Stream.Null))
            {
                writer.AutoFlush = true;

                FindBestMatch(whisperOffsets, whisperSrtFile.Subtitles.ToArray(), 1, distanceMax, minimumConfidence);

                var nb = new int[20];
                foreach (var wo in whisperOffsets.OrderBy(f => f.GlobalStartTime))
                {
                    writer.WriteLine($"--- {wo} {wo.Chunks} ---");
                    foreach (var subtitle in wo.Subtitles.OrderBy(f => f.Original.StartTime))
                    {
                        var bestMatch = subtitle.AvailableMatches.FirstOrDefault();
                        if (bestMatch.Ratio == 100)
                        {
                            writer.WriteLine($"=========================================");
                            writer.WriteLine($"=========================================  [{subtitle.ChosenOrder}] {subtitle.Original.Number} {subtitle.Original.Lines.FirstOrDefault()}");
                            writer.WriteLine($"=========================================     Ratio:{bestMatch.Ratio:D3}, D:{bestMatch.Distance.TotalMilliseconds,7}, Overlap:{bestMatch.Overlap.TotalMilliseconds,7}, {bestMatch.WO}");
                            writer.WriteLine($"=========================================");
                        }
                        else
                        {
                            writer.WriteLine($"  [{subtitle.ChosenOrder}] {subtitle.Original.Number} {subtitle.Original.Lines.FirstOrDefault()} => {subtitle.BestMatch.WO.Chunks}");
                            foreach (var match in subtitle.AvailableMatches)
                            {
                                writer.WriteLine($"     Ratio:{match.Ratio:D3}, D:{match.Distance.TotalMilliseconds,7}, Overlap:{match.Overlap.TotalMilliseconds,7}, {match.WO}");
                            }
                        }
                    }
                    nb[wo.Subtitles.Count]++;
                }

                var finalSrt = new SubtitleFile(outputSrtFullpath + suffix);
                var confidenceSrt = new SubtitleFile(outputConfidenceSrtFullpath + suffix);
                int index = 0;
                foreach (var offset in whisperOffsets)
                {
                    writer.WriteLine($"Nb subtiles in offset #{index++}: {offset.Subtitles.Count}");
                    finalSrt.Subtitles.AddRange(offset.GetFinalSubtitles());

                    var chunkLine = chunksSrtFile.Subtitles.FirstOrDefault(f => f.StartTime == offset.GlobalStartTime + offset.Offset)?.Lines.FirstOrDefault();
                    confidenceSrt.Subtitles.AddRange(offset.GetConfidenceSubtitles(chunkLine));
                }

                ExpandSubtitleTiming(finalSrt, confidenceSrt);

                for (int i = 0; i < nb.Length; i++)
                {
                    if (nb[i] > 0)
                    {
                        writer.WriteLine($"Nb offset with {i} subtitles = {nb[i]}");
                    }
                }

                WriteInfo($"{inputWavFullpath}: Saving a 'confidence' srt...");
                confidenceSrt.SaveSrt();

                WriteInfo($"{inputWavFullpath}: Saving final srt '{Path.GetFileName(outputSrtFullpath + suffix)}'...");
                finalSrt.SaveSrt();
            }
        }

        private void ExpandSubtitleTiming(SubtitleFile finalSrt, SubtitleFile confidenceSrt)
        {
            var oldSubtitles = finalSrt.Subtitles.ToArray();
            finalSrt.Subtitles.Clear();
            for (int i = 0; i < oldSubtitles.Length; i++)
            {
                var subtitle = oldSubtitles[i];
                var confidenceSubtitle = confidenceSrt.Subtitles.FirstOrDefault(f => f.StartTime == subtitle.StartTime && f.EndTime == subtitle.EndTime);
                var nextSubtitle = (i + 1 < oldSubtitles.Length) ? oldSubtitles[i + 1] : null;

                var newDuration = subtitle.Duration + TimeSpan.FromSeconds(0.5);
                if (newDuration < TimeSpan.FromSeconds(1.5))
                {
                    newDuration = TimeSpan.FromSeconds(1.5);
                }

                var newEndTime = subtitle.StartTime + newDuration;
                if (newEndTime > nextSubtitle?.StartTime)
                {
                    newEndTime = nextSubtitle.StartTime;
                }

                finalSrt.Subtitles.Add(new Subtitle(subtitle.StartTime, newEndTime, subtitle.Lines));
                if (confidenceSubtitle != null)
                {
                    confidenceSrt.Subtitles.Remove(confidenceSubtitle);
                    confidenceSrt.Subtitles.Add(new Subtitle(subtitle.StartTime, newEndTime, confidenceSubtitle.Lines));
                }
            }
        }

        private static void FindBestMatch(WorkingOffset[] offsets, Subtitle[] subtitles, int chosenOrder, TimeSpan distanceMax, int minimumConfidence)
        {
            var matches = subtitles
                .SelectMany(subtitle => offsets.Select(wo => new MyMatch(subtitle, wo))
                .Where(match => match.Distance < distanceMax))
                .ToArray();
            if (matches.Length == 0 && offsets.Length * subtitles.Length > 0)
            {
                matches = subtitles
                .SelectMany(subtitle => offsets.Select(wo => new MyMatch(subtitle, wo)))
                .ToArray();
            }
            if (matches.Length == 0)
            {
                return;
            }
            var bestMatches = matches
                .OrderByDescending(f => f.Ratio < minimumConfidence ? 0 : f.Ratio)
                .ThenBy(f => f.Distance)
                .ThenByDescending(f => f.Overlap);
            var bestMatch = bestMatches.FirstOrDefault();
            bestMatch.WO.Add(new WorkingSubtitle
            {
                ChosenOrder = chosenOrder,
                AvailableMatches = bestMatches.Where(f => f.Subtitle == bestMatch.Subtitle).ToArray()
            });

            var beforeSubtitles = subtitles.Where(f => f.Number < bestMatch.Subtitle.Number).ToArray();
            FindBestMatch(
                offsets.Where(f => f.GlobalStartTime <= bestMatch.WO.GlobalStartTime).ToArray(),
                beforeSubtitles,
                chosenOrder + 1,
                distanceMax,
                minimumConfidence);
            FindBestMatch(
                offsets.Where(f => f.GlobalStartTime >= bestMatch.WO.GlobalStartTime).ToArray(),
                subtitles.Where(f => f.Number > bestMatch.Subtitle.Number).ToArray(),
                chosenOrder + 1 + beforeSubtitles.Length,
                distanceMax,
                minimumConfidence);
        }

        public class WorkingSubtitle
        {
            public int ChosenOrder { get; internal set; }
            public MyMatch BestMatch => this.AvailableMatches.First();
            public Subtitle Original => this.BestMatch.Subtitle;
            public MyMatch[] AvailableMatches { get; set; }
        }

        public class WorkingOffset
        {
            private readonly AudioOffset r_original;

            public TimeSpan GlobalStartTime => r_original.StartTime;
            public TimeSpan GlobalEndTime => r_original.EndTime;
            public TimeSpan Offset => r_original.Offset.Value;

            public TimeSpan GlobalDuration => r_original.Duration;

            public double PercentageFilled => this.Subtitles.Sum(s => s.Original.Duration.TotalMilliseconds) / r_original.Duration.TotalMilliseconds * 100;

            public TimeSpan SpaceLeft => TimeSpan.FromMilliseconds(r_original.Duration.TotalMilliseconds - this.Subtitles.Sum(s => s.Original.Duration.TotalMilliseconds));

            public List<WorkingSubtitle> Subtitles { get; }
            public string Chunks { get; private set; }

            public WorkingOffset(AudioOffset original, IEnumerable<Subtitle> chunksSubtitles)
            {
                r_original = original;
                this.Subtitles = new List<WorkingSubtitle>();

                this.Chunks = string.Join(
                    string.Empty, 
                    chunksSubtitles
                    .Where(s => s.StartTime >= original.StartTime + original.Offset.Value && s.StartTime < original.EndTime + original.Offset.Value)
                    .SelectMany(s => s.Lines));
            }

            public void Add(WorkingSubtitle subtitle)
            {
                this.Subtitles.Add(subtitle);

                foreach (var letter in subtitle.Original.Lines.SelectMany(f => f))
                {
                    var index = this.Chunks.IndexOf(letter);
                    if (index >= 0)
                    {
                        this.Chunks = this.Chunks.Remove(index);
                    }
                }
            }

            public IEnumerable<Subtitle> GetFinalSubtitles()
            {
                TimeSpan? lastEndTime = null;
                var index = 0;
                foreach (var subtitle in this.Subtitles.Select(f => f.Original).OrderBy(f => f.StartTime))
                {
                    var startTime = lastEndTime ?? this.GlobalStartTime + r_original.Offset.Value;
                    var endTime = startTime + subtitle.Duration;
                    if (endTime > this.GlobalEndTime + r_original.Offset.Value || index++ == this.Subtitles.Count - 1)
                    {
                        endTime = this.GlobalEndTime + r_original.Offset.Value;
                    }
                    lastEndTime = endTime;

                    yield return new Subtitle(
                        startTime, 
                        endTime, 
                        subtitle.Lines);
                }
            }

            public IEnumerable<Subtitle> GetConfidenceSubtitles(string chunkLine)
            {
                if (this.Subtitles.Count == 0)
                {
                    yield return new Subtitle(
                        r_original.StartTime + r_original.Offset.Value,
                        r_original.EndTime + r_original.Offset.Value,
                        new[] { $"[No Subtitle found in this range] {chunkLine}" });
                }
                else
                {
                    TimeSpan? lastEndTime = null;
                    var indexInOffset = 0;
                    var index = 0;
                    foreach (var subtitle in this.Subtitles.OrderBy(f => f.Original.StartTime))
                    {
                        var startTime = lastEndTime ?? this.GlobalStartTime + r_original.Offset.Value;
                        var endTime = startTime + subtitle.Original.Duration;
                        if (endTime > this.GlobalEndTime + r_original.Offset.Value || index++ == this.Subtitles.Count - 1)
                        {
                            endTime = this.GlobalEndTime + r_original.Offset.Value;
                        }
                        lastEndTime = endTime;

                        var match = subtitle.AvailableMatches.First();
                        yield return new Subtitle(
                            startTime,
                            endTime,
                            $"Confidence: {match.Ratio}, ChosenOrder: {subtitle.ChosenOrder}, indexInOffset: {indexInOffset}, Overlap: {match.Overlap}, Distance: {match.Distance}, Original Line: {subtitle.Original.Lines.FirstOrDefault()}, Chunk at the time: {subtitle.BestMatch.WO.Chunks}");
                        indexInOffset++;
                    }
                }
            }

            public override string ToString()
            {
                return $"{this.GlobalStartTime,-16}-{this.GlobalEndTime,-16}, {this.GlobalStartTime + r_original.Offset.Value,-16}-{this.GlobalEndTime + r_original.Offset.Value,-16}: {this.Subtitles.Where(f => f.Original.Duration != TimeSpan.Zero).Count()} subtitles, Filled%: {(int)this.PercentageFilled,3}, SpaceLeft: {this.SpaceLeft}, {string.Join(" + ", this.Subtitles.Where(f => f.Original.Duration != TimeSpan.Zero).Select(f => f.Original.Number))}";
            }
        }

        internal class MyMatch
        {
            public Subtitle Subtitle { get; }
            public WorkingOffset WO {get;}

            public TimeSpan Overlap { get; }
            public TimeSpan Distance { get; }
            public int Ratio { get; }

            public MyMatch(Subtitle subtitle, WorkingOffset wo)
            {
                this.Subtitle = subtitle;
                this.WO = wo;

                this.Overlap = ComputeOverlap(subtitle, wo);
                this.Distance = this.Overlap == TimeSpan.Zero ? ComputeDistance(subtitle, wo) : TimeSpan.Zero;

                var mergedLines = string.Join(string.Empty, subtitle.Lines);
                this.Ratio = Fuzz.Ratio(wo.Chunks, mergedLines);
            }

            private static TimeSpan ComputeOverlap(Subtitle subtitle, WorkingOffset offset)
            {
                if (subtitle == null || offset == null)
                    return TimeSpan.Zero;

                var start = Max(subtitle.StartTime, offset.GlobalStartTime);
                var end = Min(subtitle.EndTime, offset.GlobalEndTime);

                return start <= end ? end - start : TimeSpan.Zero;
            }

            private static TimeSpan Min(TimeSpan timeA, TimeSpan timeB)
            {
                return timeA < timeB ? timeA : timeB;
            }

            private static TimeSpan Max(TimeSpan timeA, TimeSpan timeB)
            {
                return timeA > timeB ? timeA : timeB;
            }

            private static TimeSpan ComputeDistance(Subtitle subtitle, WorkingOffset offset)
            {
                if (subtitle.EndTime < offset.GlobalStartTime)
                {
                    return offset.GlobalStartTime - subtitle.EndTime;
                }
                else if (subtitle.StartTime > offset.GlobalEndTime)
                {
                    return subtitle.StartTime - offset.GlobalEndTime;
                }
                else
                {
                    return TimeSpan.Zero;
                }
            }
        }
    }
}
