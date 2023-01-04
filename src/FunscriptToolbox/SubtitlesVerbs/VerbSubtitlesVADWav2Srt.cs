using AudioSynchronization;
using CommandLine;
using FunscriptToolbox.Core;
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

            [Option('l', "transcribedlanguage", Required = false, HelpText = "Transcribed langage, will be used for output file (.<lang>.srt)", Default = "jp")]
            public string TranscribedLanguage { get; set; }
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
                    var outputSrtFullpath = Path.ChangeExtension(inputWavFullpath, $".{r_options.TranscribedLanguage}.srt");
                    var debugSrtFullpath = Path.ChangeExtension(inputWavFullpath, $".{r_options.TranscribedLanguage}.debug.txt");

                    if (!r_options.Force && File.Exists(outputSrtFullpath))
                    {
                        WriteInfo($"{inputWavFullpath}: Skipping because '{Path.GetFileName(outputSrtFullpath)}' already exists (use --force to override).");
                        continue;
                    }
                    if (!File.Exists(inputSrtFullpath) || !File.Exists(inputOffsetFullpath))
                    {
                        WriteInfo($"{inputWavFullpath}: Skipping because can't find a file named '{Path.GetFileName(inputSrtFullpath)}'and '{Path.GetFileName(inputOffsetFullpath)}'.");
                        continue;
                    }

                    var watch = Stopwatch.StartNew();

                    WriteInfo($"{inputWavFullpath}: Loading input srt...");
                    var whisperSrtFile = SubtitleFile.FromSrtFile(inputSrtFullpath);

                    WriteInfo($"{inputWavFullpath}: Loading input offset...");
                    AudioOffset[] whisperOffsets;
                    using (var reader = File.OpenText(inputOffsetFullpath))
                    using (var jsonReader = new JsonTextReader(reader))
                    {
                        whisperOffsets = Serializer.Deserialize<AudioOffset[]>(jsonReader);
                    }

                    WriteInfo($"{inputWavFullpath}: Applying offsets...");
                    var finalSrt = new SubtitleFile(outputSrtFullpath);

                    var temps = whisperOffsets.Select(wo => new WorkingOffset(wo)).ToArray();
                    WorkingOffset lastOffset = null;
                    using (var writer = File.CreateText(debugSrtFullpath))
                    {
                        writer.AutoFlush = true;

                        foreach (var subtitle in whisperSrtFile.Subtitles)
                        {
                            var candidates = temps
                                .Select(t => new { Duration = ComputeOverlap(subtitle, t), Offset = t })
                                .Where(f => f.Duration != null)
                                .ToList();

                            writer.WriteLine();
                            writer.WriteLine($"----- {subtitle.Number} {subtitle.Lines[0]} -----");
                            var lastFilled = temps.LastOrDefault(f => f.PercentageFilled > 0.0);
                            if (lastFilled != null)
                            {
                                var show = true;
                                foreach (var temp in temps)
                                {
                                    if (show)
                                    {
                                        writer.WriteLine($"CurrentState: {temp}");
                                    }
                                    if (temp == lastFilled)
                                    {
                                        show = false;
                                    }
                                }
                                writer.WriteLine();
                            }

                            writer.WriteLine($"Subtitle {subtitle.StartTime}, {subtitle.EndTime}, {subtitle.Duration}");
                            foreach (var overlap in candidates)
                            {
                                writer.WriteLine($"    Choice: {overlap}");
                            }

                            if (candidates.Count == 0)
                            {
                                writer.WriteLine($"[SubtitleInGAP]");
                                lastOffset?.Add(subtitle, "[SubtitleInGAP]");
                            }
                            else if (candidates.Count == 1)
                            {
                                var overlap = candidates.First();
                                writer.WriteLine($"Adding subtitle-{subtitle.Number} in offset {overlap.Offset}");
                                overlap.Offset.Add(subtitle);
                            }
                            else
                            {
                                foreach (var c in candidates.ToArray())
                                {
                                    if (c.Offset.Subtitles.Count > 0 && c.Offset.SpaceLeft > TimeSpan.Zero)
                                    {
                                        var percentage = subtitle.Duration.TotalMilliseconds / c.Offset.SpaceLeft.TotalMilliseconds * 100;
                                        if (percentage > 100)
                                        {
                                            candidates.Remove(c);
                                        }
                                    }
                                }

                                if (candidates.Count == 1)
                                {
                                    var overlap = candidates.First();
                                    writer.WriteLine($"[Multiple-TakeOnlyRemaining] Adding subtitle-{subtitle.Number} in offset {overlap.Offset}");
                                    overlap.Offset.Add(subtitle, "[Multiple-TakeOnlyRemaining]");
                                }
                                else
                                {
                                    var overlap = candidates.First();
                                    writer.WriteLine($"[Multiple-TakeFirst] Adding subtitle-{subtitle.Number} in offset {overlap.Offset}");
                                    overlap.Offset.Add(subtitle, "[Multiple-TakeFirst]");
                                }
                            }
                        }

                        foreach (var temp in temps)
                        {
                            var subtitles = temp.GetFinalSubtitles();
                            finalSrt.Subtitles.AddRange(subtitles);
                        }

                        WriteInfo($"{inputWavFullpath}: Saving final srt '{Path.GetFileName(outputSrtFullpath)}'...");
                        finalSrt.SaveSrt();

                        WriteInfo($"{inputWavFullpath}: Finished in {watch.Elapsed}.");
                        WriteInfo();
                    }
                }
                catch (Exception ex)
                {
                    WriteError($"{inputWavFullpath}: Exception occured: {ex}");
                    WriteInfo();
                }
            }

            return base.NbErrors;
        }


        public class WorkingOffset
        {
            private readonly AudioOffset r_original;

            public TimeSpan GlobalStartTime => r_original.StartTime;
            public TimeSpan GlobalEndTime => r_original.EndTime;

            public TimeSpan GlobalDuration => r_original.Duration;

            public double PercentageFilled => this.TempSubtitles.Sum(s => s.Duration.TotalMilliseconds) / r_original.Duration.TotalMilliseconds * 100;

            public TimeSpan SpaceLeft => TimeSpan.FromMilliseconds(r_original.Duration.TotalMilliseconds - this.TempSubtitles.Sum(s => s.Duration.TotalMilliseconds));

            public List<Subtitle> Subtitles { get; }
            public List<Subtitle> TempSubtitles { get; }

            public WorkingOffset(AudioOffset original)
            {
                r_original = original;
                this.Subtitles = new List<Subtitle>();
                this.TempSubtitles = new List<Subtitle>();
            }

            public void Add(Subtitle subtitle, string comment = null)
            {
                if (comment != null)
                    this.Subtitles.Add(new Subtitle(subtitle.StartTime, subtitle.StartTime, comment));
                this.Subtitles.Add(subtitle);

                this.TempSubtitles.Clear();
                TimeSpan lastEndTime = TimeSpan.Zero;
                foreach (var sub in this.Subtitles)
                {
                    var startTime = (this.TempSubtitles.Count == 0)
                        ? this.GlobalStartTime + r_original.Offset.Value
                        : lastEndTime;
                    var endTime = startTime + sub.Duration;
                    if (endTime > this.GlobalEndTime + r_original.Offset.Value)
                    {
                        endTime = this.GlobalEndTime + r_original.Offset.Value;
                    }
                    lastEndTime = endTime;

                    this.TempSubtitles.Add(new Subtitle(startTime, endTime, sub.Lines, sub.Number));
                }
            }

            public IEnumerable<Subtitle> GetFinalSubtitles()
            {
                if (this.TempSubtitles.Count == 0)
                {
                    yield return new Subtitle(
                        r_original.StartTime + r_original.Offset.Value,
                        r_original.EndTime + r_original.Offset.Value,
                        new[] { $"[No Subtitle found in this range]" });
                }
                else
                {
                    for (int i = 0; i < this.TempSubtitles.Count; i++)
                    {
                        var sub = this.TempSubtitles[i];
                        if (i < this.TempSubtitles.Count - 1)
                        {
                            yield return sub;
                        }
                        else
                        {
                            yield return new Subtitle(
                                sub.StartTime,
                                this.GlobalEndTime + r_original.Offset.Value,
                                sub.Lines,
                                sub.Number);
                        }
                    }
                }
            }

            public override string ToString()
            {
                return $"{this.GlobalStartTime,-16}-{this.GlobalEndTime,-16}, {this.GlobalStartTime + r_original.Offset.Value,-16}-{this.GlobalEndTime + r_original.Offset.Value,-16}: {this.TempSubtitles.Where(f => f.Duration != TimeSpan.Zero).Count()} subtitles, {(int)this.PercentageFilled,3}, {this.SpaceLeft}, {string.Join(" + ", this.TempSubtitles.Where(f => f.Duration != TimeSpan.Zero).Select(f => f.Number))}";
            }
        }

        private static TimeSpan? ComputeOverlap(Subtitle subtitle, WorkingOffset offset)
        {
            if (subtitle == null || offset == null)
                return null;

            var start = Max(subtitle.StartTime, offset.GlobalStartTime);
            var end = Min(subtitle.EndTime, offset.GlobalEndTime);

            return start <= end ? (TimeSpan?) end - start : null;
        }

        private static TimeSpan Min(TimeSpan timeA, TimeSpan timeB)
        {
            return timeA < timeB ? timeA : timeB;
        }

        private static TimeSpan Max(TimeSpan timeA, TimeSpan timeB)
        {
            return timeA > timeB ? timeA : timeB;
        }
    }
}
