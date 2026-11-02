using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{

    /// <summary>
    /// Replays the refining with other settings, WITHOUT calling the AI: the final timings of a real run are the truth,
    /// and the "AI" answers with the tightest option that does not cut the truth (or Max when none does).
    /// Measures what would be sent: requests, audio duration, Missing Start/End...
    /// </summary>
    public class TimingSimulator
    {
        public class Truth
        {
            public TimedItemWithMetadata Item;
            public string Text;
            public TimeSpan Start;
            public TimeSpan End;
        }

        public class SideReport
        {
            public BoundarySide Side;
            public int Iterations;
            public int Requests;
            public int Clips;
            public double AudioSeconds;
        }

        public class Result
        {
            public string Name;
            public int Nodes;
            public List<SideReport> Sides = new List<SideReport>();
            public TimingRefinerStats Stats;

            public int Requests => Sides.Sum(s => s.Requests);
            public int Clips => Sides.Sum(s => s.Clips);
            public double AudioSeconds => Sides.Sum(s => s.AudioSeconds);
            public int Count(ValidationCode code) => Stats != null && Stats.NoteCounts.TryGetValue(code, out var n) ? n : 0;
        }

        /// <summary>An option this close inside the truth is still considered as not cutting it.</summary>
        public TimeSpan Tolerance { get; set; } = TimeSpan.FromMilliseconds(30);

        private readonly PcmAudio r_audio;
        private readonly TimingValidatorSettings r_validator;
        private readonly Transcription r_template;
        private readonly List<Truth> r_truths;
        private readonly Dictionary<float, AudioTerrainMapping> r_terrains = new Dictionary<float, AudioTerrainMapping>();

        /// <param name="terrain">The terrain of the real run (reused while ValleyReach is the same, otherwise recomputed once per value).</param>
        public TimingSimulator(
            PcmAudio audio,
            AudioTerrainMapping terrain,
            float terrainValleyReach,
            TimingValidatorSettings validator,
            Transcription template,
            IEnumerable<Truth> truths)
        {
            r_audio = audio;
            r_validator = validator;
            r_template = template;
            r_truths = truths.OrderBy(t => t.Item.StartTime).ToList();
            if (terrain != null)
                r_terrains[terrainValleyReach] = terrain;
        }

        public Result Run(string name, TimingRefinerSettings settings)
        {
            r_terrains.TryGetValue(settings.ValleyReach, out var terrain);
            var refiner = new TimingRefiner(r_audio, settings, r_validator, terrain);
            r_terrains[settings.ValleyReach] = refiner.TerrainMapping;

            var truthByStart = new Dictionary<TimeSpan, Truth>();
            foreach (var t in r_truths)
            {
                refiner.AddCandidate(t.Item, t.Text ?? "");
                truthByStart[t.Item.StartTime] = t;
            }

            var result = new Result { Name = name, Nodes = r_truths.Count };

            foreach (var side in TimingCandidate.Sides)
            {
                var report = new SideReport { Side = side };
                result.Sides.Add(report);

                refiner.BeginPhase(side);
                while (true)
                {
                    refiner.PrepareNextRound(side);
                    var toTest = refiner.Candidates.Where(c => c.CurrentRound != null).ToList();
                    if (toTest.Count == 0)
                        break;

                    report.Iterations++;
                    var response = new Transcription(
                        id: r_template.Id,
                        metadataAlwaysProduced: side == BoundarySide.Start ? TimingMeta.BestStart : TimingMeta.BestEnd,
                        privateMetadataNames: null,
                        language: r_template.Language,
                        isFinished: false);

                    foreach (var cand in toTest)
                    {
                        var round = cand.CurrentRound;
                        report.Requests++;
                        foreach (var clip in round.Clips)
                        {
                            report.Clips++;
                            report.AudioSeconds += (clip.End - clip.Start).TotalSeconds;
                        }

                        var truth = truthByStart[cand.OriginalStartTime];
                        var meta = new MetadataCollection();
                        meta[side == BoundarySide.Start ? TimingMeta.BestStart : TimingMeta.BestEnd] =
                            Answer(round, side == BoundarySide.Start ? truth.Start : truth.End);
                        response.AddNewItem(cand.OriginalStartTime, cand.OriginalEndTime, meta);
                    }

                    refiner.ProcessBatchResults(response.Items, side);
                }
                refiner.EndPhase(side);
            }

            refiner.FinalizeOverlaps();
            result.Stats = refiner.ComputeStats();
            return result;
        }

        /// <summary>The tightest option that does not cut the truth; "Max" when none does.</summary>
        private string Answer(TimingRound round, TimeSpan truth)
        {
            bool isStart = round.Side == BoundarySide.Start;
            var valid = round.Options
                .Where(kv => !string.Equals(kv.Key, "Max", StringComparison.OrdinalIgnoreCase))
                .Where(kv => isStart ? kv.Value <= truth + Tolerance : kv.Value >= truth - Tolerance)
                .ToList();

            if (valid.Count == 0)
                return "Max";

            return (isStart ? valid.OrderByDescending(kv => kv.Value) : valid.OrderBy(kv => kv.Value)).First().Key;
        }

        // ---------------------------------------------------------------------
        // Report
        // ---------------------------------------------------------------------

        /// <param name="audioTokensPerSecond">Only for the estimate (Gemini: 32). Calibrate it with your real run: real input tokens / the audio seconds of the first result.</param>
        public static void Print(SubtitleGeneratorContext context, IReadOnlyList<Result> results, double audioTokensPerSecond, Action<string> write = null)
        {
            write = write ?? (message => context.WriteInfo(message));
            const string line = "================================================================================";
            write(line);
            write("  TIMING SIMULATION (fake AI: answers from the final timings of this run, no cache)");
            write(line);

            var baseline = results[0];
            foreach (var r in results)
            {
                var hours = r.AudioSeconds / 3600.0;
                write($"  {r.Name}");
                write($"    Requests:      {r.Requests,6}  ({(double)r.Requests / Math.Max(1, r.Nodes):0.00}/node)  " +
                    string.Join("  ", r.Sides.Select(s => $"{s.Side}: {s.Requests} in {s.Iterations} it.")));
                write($"    Audio sent:    {(int)hours}h{(r.AudioSeconds / 60.0) % 60:00}m  ({r.Clips} clips, " +
                    $"avg {r.AudioSeconds / Math.Max(1, r.Clips):0.00}s/clip)  " +
                    string.Join("  ", r.Sides.Select(s => $"{s.Side}: {s.AudioSeconds / 60.0:0}min")));
                write($"    ~Audio tokens: {r.AudioSeconds * audioTokensPerSecond / 1e6:0.00}M");
                write($"    MissingStart: {r.Count(ValidationCode.MissingStart)}   MissingEnd: {r.Count(ValidationCode.MissingEnd)}   " +
                    $"Unfinished: {r.Count(ValidationCode.Unfinished)}   Review: {r.Stats?.Review ?? 0}/{r.Nodes}");
                if (r != baseline)
                {
                    write($"    vs baseline:   requests {Delta(r.Requests, baseline.Requests)}, " +
                        $"audio {Delta(r.AudioSeconds, baseline.AudioSeconds)}");
                }
            }
            write(line);
        }

        private static string Delta(double value, double reference)
            => reference <= 0 ? "n/a" : $"{(value - reference) / reference * 100:+0.0;-0.0;0.0}%";
    }
}