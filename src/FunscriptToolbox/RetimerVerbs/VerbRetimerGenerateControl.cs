using CommandLine;
using FunscriptToolbox.Core;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.RetimerVerbs
{
    class VerbRetimerGenerateControl : VerbRetimerBase
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("retimer.generatecontrol", aliases: new[] { "retimer.gen" }, HelpText = "Create the initial .control.srt timeline blueprint.")]
        public class Options : OptionsBase
        {
            [Option("video", Required = true, HelpText = "Original video file.")]
            public string Video { get; set; }

            [Option("sub-padding-before", Default = 0.5, HelpText = "Padding in seconds before a subtitle.")]
            public double SubPaddingBefore { get; set; }

            [Option("sub-padding-after", Default = 0.5, HelpText = "Padding in seconds after a subtitle.")]
            public double SubPaddingAfter { get; set; }

            [Option("sub-gap-threshold", Default = 8.0, HelpText = "Minimum gap in seconds to generate a GAP_SUBTITLE.")]
            public double SubGapThreshold { get; set; }

            [Option("sub-density-window", Default = 30.0, HelpText = "Seconds to look before and after a subtitle to calculate density.")]
            public double SubDensityWindow { get; set; }

            [Option("sub-isolated-threshold", Default = 0.10, HelpText = "Density ratio (0.0 to 1.0). If subtitles fill less than this percentage of the window, they are flagged as isolated.")]
            public double SubIsolatedThreshold { get; set; }

            [Option("fun-padding-before", Default = 1.0, HelpText = "Padding in seconds before a funscript action.")]
            public double FunPaddingBefore { get; set; }

            [Option("fun-padding-after", Default = 1.0, HelpText = "Padding in seconds after a funscript action.")]
            public double FunPaddingAfter { get; set; }

            [Option("fun-gap-threshold", Default = 10.0, HelpText = "Threshold to merge funscript actions together.")]
            public double FunGapThreshold { get; set; }

            [Option("allow-overlaps", Default = false, HelpText = "If true, subtitles and funscript blocks can overlap in the timeline. If false, subtitles will be trimmed/removed to prevent overlapping with funscript actions.")]
            public bool AllowOverlaps { get; set; }
        }

        private readonly Options r_options;

        public VerbRetimerGenerateControl(Options options) : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            string basePath = Path.Combine(
                Path.GetDirectoryName(r_options.Video) ?? "",
                Path.GetFileNameWithoutExtension(r_options.Video));

            string subtitlePath = basePath + SubtitleFile.SrtExtension;
            string funscriptPath = basePath + Funscript.FunscriptExtension;
            string outputControlPath = basePath + ".control.srt";

            if (!File.Exists(subtitlePath) && !File.Exists(funscriptPath))
            {
                WriteError($"Could not find sidecar files automatically. Expected either {subtitlePath} or {funscriptPath} to exist.");
                return 1;
            }

            var mediaInfo = GetMediaInfo(r_options.Video);
            var videoStream = mediaInfo.VideoStreams.FirstOrDefault();
            if (videoStream == null)
            {
                WriteError("No video stream found in the input file.");
                return 1;
            }

            TimeSpan videoDuration = videoStream.Duration;
            WriteInfo($"Video Loaded. Duration: {videoDuration}, Framerate: {videoStream.Framerate}");

            var subBlocks = new List<Subtitle>();
            var funBlocks = new List<Subtitle>();

            // 1. Process Subtitles
            if (File.Exists(subtitlePath))
            {
                WriteInfo($"Processing Subtitles: {subtitlePath}");
                var subs = SubtitleFile.FromSrtFile(subtitlePath);
                subBlocks.AddRange(ProcessSubtitles(subs.Subtitles, videoDuration));
            }

            // 2. Process Funscripts
            if (File.Exists(funscriptPath))
            {
                WriteInfo($"Processing Funscript: {funscriptPath}");
                var funs = Funscript.FromFile(funscriptPath);
                funBlocks.AddRange(ProcessFunscripts(funs.Actions, videoDuration));
            }

            // 3. Resolve Overlaps (if configured)
            if (!r_options.AllowOverlaps && subBlocks.Any() && funBlocks.Any())
            {
                subBlocks = SubtractFunscriptsFromSubtitles(subBlocks, funBlocks);
            }

            // 4. Combine, Sort, and Re-Number
            var finalSubtitles = subBlocks.Concat(funBlocks)
                .OrderBy(s => s.StartTime)
                .ThenBy(s => s.EndTime)
                .Select((s, index) => new Subtitle(s.StartTime, s.EndTime, s.Lines, index + 1))
                .ToList();

            var controlSubtitleFile = new SubtitleFile(outputControlPath, finalSubtitles);
            controlSubtitleFile.SaveSrt(outputControlPath);

            WriteInfo($"Generated timeline blueprint with {finalSubtitles.Count} blocks.");
            WriteInfo($"Saved to: {outputControlPath}");

            return 0;
        }

        private IEnumerable<Subtitle> ProcessSubtitles(List<Subtitle> originals, TimeSpan videoDuration)
        {
            var results = new List<Subtitle>();
            if (originals == null || !originals.Any()) return results;

            var expandedSubs = new List<Subtitle>();

            // Pass 1: Apply Padding
            foreach (var sub in originals.OrderBy(s => s.StartTime))
            {
                var start = sub.StartTime - TimeSpan.FromSeconds(r_options.SubPaddingBefore);
                var end = sub.EndTime + TimeSpan.FromSeconds(r_options.SubPaddingAfter);

                if (start < TimeSpan.Zero) start = TimeSpan.Zero;
                if (end > videoDuration) end = videoDuration;

                expandedSubs.Add(new Subtitle(start, end, sub.Lines, sub.Number));
            }

            // Pass 2: Split overlapping boundaries
            for (int i = 0; i < expandedSubs.Count - 1; i++)
            {
                var curr = expandedSubs[i];
                var next = expandedSubs[i + 1];

                if (curr.EndTime > next.StartTime)
                {
                    // Split the difference equally
                    long midTicks = (curr.EndTime.Ticks + next.StartTime.Ticks) / 2;
                    var mid = TimeSpan.FromTicks(midTicks);

                    expandedSubs[i] = new Subtitle(curr.StartTime, mid, curr.Lines, curr.Number);
                    expandedSubs[i + 1] = new Subtitle(mid, next.EndTime, next.Lines, next.Number);
                }
            }

            // Pass 3: Gather expanded text, inject {FILLED_GAP_SUBTITLE}, and Density check
            TimeSpan currentTimeline = TimeSpan.Zero;
            TimeSpan halfWindow = TimeSpan.FromSeconds(r_options.SubDensityWindow);

            for (int i = 0; i < expandedSubs.Count; i++)
            {
                var sub = expandedSubs[i];

                // --- Density Calculation ---
                // 1. Find midpoint of the current block to anchor the window
                long subMidTicks = sub.StartTime.Ticks + (sub.Duration.Ticks / 2);
                TimeSpan midPoint = TimeSpan.FromTicks(subMidTicks);

                var windowStart = midPoint - halfWindow;
                if (windowStart < TimeSpan.Zero) windowStart = TimeSpan.Zero;

                var windowEnd = midPoint + halfWindow;
                if (windowEnd > videoDuration) windowEnd = videoDuration;

                var windowDuration = (windowEnd - windowStart).TotalSeconds;

                // 2. Sum subtitle durations falling inside this window using the ORIGINAL untouched subtitles
                double coveredDuration = 0;
                foreach (var origSub in originals)
                {
                    // If the original subtitle overlaps with our window at all
                    if (origSub.EndTime > windowStart && origSub.StartTime < windowEnd)
                    {
                        var overlapStart = origSub.StartTime > windowStart ? origSub.StartTime : windowStart;
                        var overlapEnd = origSub.EndTime < windowEnd ? origSub.EndTime : windowEnd;
                        coveredDuration += (overlapEnd - overlapStart).TotalSeconds;
                    }
                }

                // 3. Calculate density ratio
                double density = windowDuration > 0 ? coveredDuration / windowDuration : 1.0;
                bool isIsolated = density <= r_options.SubIsolatedThreshold;
                // ---------------------------

                // Check gap before this subtitle
                var gapDuration = (sub.StartTime - currentTimeline).TotalSeconds;
                if (gapDuration > 0 && gapDuration < r_options.SubGapThreshold)
                {
                    results.Add(new Subtitle(currentTimeline, sub.StartTime, "{FILLED_GAP_SUBTITLE}"));
                }

                var lines = sub.Lines.ToList();
                if (isIsolated)
                {
                    // Add the density percentage, e.g., {ISOLATED_SUBTITLE (4%)}
                    lines.Insert(0, $"==> ISOLATED_SUBTITLE ({Math.Round(density * 100)}%)");
                }

                results.Add(new Subtitle(sub.StartTime, sub.EndTime, lines.ToArray(), sub.Number));
                currentTimeline = sub.EndTime;
            }

            // Check gap at the end of the video
            var tailDuration = (videoDuration - currentTimeline).TotalSeconds;
            if (tailDuration > 0 && tailDuration < r_options.SubGapThreshold)
            {
                results.Add(new Subtitle(currentTimeline, videoDuration, "{FILLED_GAP_SUBTITLE}"));
            }

            return results;
        }

        private IEnumerable<Subtitle> ProcessFunscripts(FunscriptAction[] actions, TimeSpan videoDuration)
        {
            var results = new List<Subtitle>();
            if (actions == null || !actions.Any()) return results;

            var rawBlocks = new List<Tuple<TimeSpan, TimeSpan>>();

            // Pass 1: Apply padding to all actions
            foreach (var action in actions)
            {
                var start = action.AtAsTimeSpan - TimeSpan.FromSeconds(r_options.FunPaddingBefore);
                var end = action.AtAsTimeSpan + TimeSpan.FromSeconds(r_options.FunPaddingAfter);

                if (start < TimeSpan.Zero) start = TimeSpan.Zero;
                if (end > videoDuration) end = videoDuration;

                rawBlocks.Add(Tuple.Create(start, end));
            }

            rawBlocks = rawBlocks.OrderBy(b => b.Item1).ToList();
            var mergedBlocks = new List<Tuple<TimeSpan, TimeSpan>>();

            // Pass 2: Merge overlapping blocks OR blocks within the GapThreshold
            foreach (var block in rawBlocks)
            {
                if (!mergedBlocks.Any())
                {
                    mergedBlocks.Add(block);
                    continue;
                }

                var lastBlock = mergedBlocks.Last();
                var gap = (block.Item1 - lastBlock.Item2).TotalSeconds;

                if (gap < r_options.FunGapThreshold)
                {
                    // Merge
                    var newEnd = block.Item2 > lastBlock.Item2 ? block.Item2 : lastBlock.Item2;
                    mergedBlocks[mergedBlocks.Count - 1] = Tuple.Create(lastBlock.Item1, newEnd);
                }
                else
                {
                    mergedBlocks.Add(block);
                }
            }

            // Pass 3: Emit FUNSCRIPT_ACTIONS blocks
            foreach (var block in mergedBlocks)
            {
                results.Add(new Subtitle(block.Item1, block.Item2, "{FUNSCRIPT_ACTIONS}"));
            }

            return results;
        }

        private List<Subtitle> SubtractFunscriptsFromSubtitles(List<Subtitle> subtitles, List<Subtitle> funscripts)
        {
            var results = new List<Subtitle>();

            foreach (var sub in subtitles)
            {
                var currentPieces = new List<Subtitle> { sub };

                foreach (var fun in funscripts)
                {
                    var nextPieces = new List<Subtitle>();

                    foreach (var piece in currentPieces)
                    {
                        // If there is no overlap at all, keep the piece as is
                        if (piece.EndTime <= fun.StartTime || piece.StartTime >= fun.EndTime)
                        {
                            nextPieces.Add(piece);
                        }
                        else
                        {
                            // An overlap exists. We might need to split the piece into a left and right chunk.

                            // Left remaining chunk?
                            if (piece.StartTime < fun.StartTime)
                            {
                                nextPieces.Add(new Subtitle(piece.StartTime, fun.StartTime, piece.Lines, piece.Number));
                            }

                            // Right remaining chunk?
                            if (piece.EndTime > fun.EndTime)
                            {
                                nextPieces.Add(new Subtitle(fun.EndTime, piece.EndTime, piece.Lines, piece.Number));
                            }
                        }
                    }

                    // Move the cut pieces forward for the next funscript block check
                    currentPieces = nextPieces;
                }

                results.AddRange(currentPieces);
            }

            return results;
        }
    }
}