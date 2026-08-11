using CommandLine;
using FunscriptToolbox.Core;
using FunscriptToolbox.Core.Infra;
using log4net;
using Newtonsoft.Json;
using System.Collections.Generic;
using System;
using System.Linq;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Xabe.FFmpeg;

namespace FunscriptToolbox.RetimerVerbs
{
    [JsonObject(IsReference = false)]
    class VerbRetimerRenderVideo : VerbRetimerBase
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly Regex rs_speedRegex = new Regex(@"\{Speed:\s*([\d\.]+)\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        [Verb("retimer.rendervideo", aliases: new[] { "retimer.render" }, HelpText = "Render a variable speed video based on a .control.srt blueprint.")]
        public class Options : OptionsBase
        {
            [Option("original-video", Required = true, HelpText = "Original input video file.")]
            public string OriginalVideo { get; set; }

            [Option("retimed-video", Required = false, HelpText = "Output video file. Defaults to <input>.retimed.mp4 if not specified.")]
            public string RetimedVideo { get; set; }

            [Option("default-speed-controlled", Default = 1.0, HelpText = "Speed multiplier for sections covered by the .control.srt (can be overridden by {Speed: X.X} in subs).")]
            public double DefaultSpeedControlled { get; set; }

            [Option("default-speed-uncontrolled", Default = 10.0, HelpText = "Base speed multiplier for sections NOT covered by subtitles.")]
            public double DefaultSpeedUncontrolled { get; set; }

            [Option("max-uncontrolled-duration", Default = 15.0, HelpText = "Maximum allowed output duration (in seconds) for a fast-forwarded section. If it would take longer, the speed is increased dynamically.")]
            public double MaxUncontrolledDuration { get; set; }

            [Option("micro-gap-threshold", Default = 0.05, HelpText = "Gaps smaller than this (in seconds) between controlled blocks are absorbed to prevent tiny framerate jumps (SubtitleEdit usually leaves 30ms gaps).")]
            public double MicroGapThreshold { get; set; }

            [Option("encoding-video", Default = "-c:v libx265 -crf 20 -tag:v hvc1", HelpText = "FFmpeg parameters for video encoding.")]
            public string EncodingVideo { get; set; }

            [Option("encoding-audio", Default = "-c:a aac", HelpText = "FFmpeg parameters for audio encoding.")]
            public string EncodingAudio { get; set; }
        }

        private readonly Options r_options;

        public VerbRetimerRenderVideo(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            string controlFile = Path.ChangeExtension(r_options.OriginalVideo, ".control.srt");
            string retimedVideo = string.IsNullOrWhiteSpace(r_options.RetimedVideo)
                ? Path.ChangeExtension(r_options.OriginalVideo, ".retimed.mp4")
                : r_options.RetimedVideo;

            if (!File.Exists(r_options.OriginalVideo))
            {
                WriteError($"Original video not found: {r_options.OriginalVideo}");
                return 1;
            }

            if (!File.Exists(controlFile))
            {
                WriteError($"Control blueprint not found: {controlFile}. Please run retimer.generatecontrol first.");
                return 1;
            }

            ProcessVideoWithSubtitleSpeed(controlFile, r_options.OriginalVideo, retimedVideo);
            return 0;
        }

        public void ProcessVideoWithSubtitleSpeed(string subtitleFilePath, string originalVideoFilePath, string retimedVideoFilePath)
        {
            var subtitleFile = SubtitleFile.FromSrtFile(subtitleFilePath);

            // Get exact media info using Xabe.FFmpeg
            var mediaInfo = GetMediaInfo(originalVideoFilePath);
            var videoStream = mediaInfo.VideoStreams.FirstOrDefault();

            if (videoStream == null)
            {
                WriteError("No video stream found in the input file.");
                return;
            }

            TimeSpan videoDuration = videoStream.Duration;
            double fps = videoStream.Framerate;

            WriteInfo($"Video loaded. Duration: {videoDuration}, Framerate: {fps} fps");

            var segments = GenerateSpeedSegments(
                subtitleFile.Subtitles,
                videoDuration,
                fps);

            ProcessVideoPerfectSync(originalVideoFilePath, retimedVideoFilePath, segments, fps);
        }

        private void ProcessVideoPerfectSync(string inputVideo, string outputVideo, List<SpeedSegment> segments, double fps)
        {
            var offsets = new List<SyncOffsetDto>();
            TimeSpan currentOutputTime = TimeSpan.Zero;
            List<string> tempFiles = new List<string>();

            WriteInfo($"Starting empirical segment extraction for {segments.Count} segments...");
            WriteInfo($"Estimated final duration: {segments.Sum(f => f.EstimatedFinalDuration)}s");

            string fpsString = fps.ToString(CultureInfo.InvariantCulture);

            // Grab the total timeline duration so we can calculate the percentage progress
            double totalVideoDuration = segments.Count > 0 ? segments.Last().EndTime.TotalSeconds : 1.0;

            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                if (segment.Duration <= TimeSpan.Zero) continue;

                double inputDurationSecs = segment.Duration.TotalSeconds;
                double targetSpeed = segment.Speed;

                // Calculate Input Frames vs Output expectations
                int exactInputFrameCount = (int)Math.Round(inputDurationSecs * fps);
                double expectedOutputDurationSecs = inputDurationSecs / targetSpeed;
                int exactOutputFrameCount = (int)Math.Round(expectedOutputDurationSecs * fps);

                if (exactOutputFrameCount == 0)
                {
                    WriteError($"Skipping Seg {i + 1}/{segments.Count} - Too short to render at {targetSpeed:F2}x");
                    continue;
                }

                double exactOutputDurationSecs = exactOutputFrameCount / fps;
                double effectiveSpeedFactor = inputDurationSecs / exactOutputDurationSecs;

                // -- PRESENTATION LOGIC --
                double progressPercent = (segment.StartTime.TotalSeconds / totalVideoDuration) * 100.0;
                string timeInfo = $"[{segment.StartTime:hh\\:mm\\:ss\\.fff} | {progressPercent,5:F1}%]";

                bool isSpeedOne = Math.Abs(targetSpeed - 1.0) < 0.001;
                if (isSpeedOne)
                {
                    // Force exactly 1.0 math to prevent floating point noise on exact 1.0x segments
                    effectiveSpeedFactor = 1.0;
                    WriteInfo($"Encoding Seg {i + 1,2}/{segments.Count} {timeInfo} [Speed: 1.00x] {exactInputFrameCount} frames");
                }
                else
                {
                    WriteInfo($"Encoding Seg {i + 1,2}/{segments.Count} {timeInfo} [Target: {targetSpeed:F2}x | Actual: {effectiveSpeedFactor:F2}x] {exactInputFrameCount} => {exactOutputFrameCount} frames");
                }
                // ------------------------

                string tempFile = Path.ChangeExtension(outputVideo, $".segment_{i:D3}.mkv");
                tempFiles.Add(tempFile);

                double videoPtsFactor = 1.0 / effectiveSpeedFactor;
                string vPtsStr = videoPtsFactor.ToString(CultureInfo.InvariantCulture);

                string vFilter = $"[0:v]setpts={vPtsStr}*(PTS-STARTPTS),fps={fpsString}:round=down[v]";
                string aFilter = $"[0:a]asetpts=PTS-STARTPTS,{GetAudioTempoFilter(effectiveSpeedFactor)}[a]";

                var conversion = FFmpeg.Conversions.New()
                    .SetOverwriteOutput(true)
                    .AddParameter($"-ss {segment.StartTime}")
                    .AddParameter($"-i \"{inputVideo}\"")
                    .AddParameter($"-filter_complex \"{vFilter};{aFilter}\"")
                    .AddParameter("-map \"[v]\" -map \"[a]\"")
                    .AddParameter($"-frames:v {exactOutputFrameCount}")
                    .AddParameter($"-t {TimeSpan.FromSeconds(exactOutputDurationSecs)}")
                    .AddParameter(r_options.EncodingVideo)
                    .AddParameter("-c:a pcm_s16le");

                StartAndHandleFfmpegProgress(conversion, tempFile);

                // -- VALIDATION CHECK --
                var tempInfo = GetMediaInfo(tempFile);

                // We check against a 5ms tolerance to account for Xabe.FFmpeg rounding the container duration
                double diffSecs = Math.Abs(tempInfo.Duration.TotalSeconds - exactOutputDurationSecs);
                if (diffSecs > 0.005)
                {
                    WriteError($"Segment {i + 1} Duration Mismatch! Expected: {exactOutputDurationSecs:F3}s, Actual File: {tempInfo.Duration.TotalSeconds:F3}s (Diff: {diffSecs:F3}s)");
                }

                TimeSpan actualVideoDuration = TimeSpan.FromSeconds(exactOutputDurationSecs);
                offsets.Add(new SyncOffsetDto
                {
                    OriginalStartTime = segment.StartTime,
                    OriginalEndTime = segment.EndTime,
                    RetimerStartTime = currentOutputTime,
                    RetimerEndTime = currentOutputTime + actualVideoDuration
                });

                currentOutputTime += actualVideoDuration;
            }

            string offsetsJsonFile = outputVideo.Replace(".mp4", ".offsets.json");
            File.WriteAllText(
                offsetsJsonFile,
                JsonConvert.SerializeObject(
                    offsets,
                    Formatting.Indented,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }
                ));

            WriteInfo($"Wrote synchronization offsets to: {offsetsJsonFile}");

            WriteInfo("Concatenating segments...");

            var concatFilePath = Path.ChangeExtension(outputVideo, $".concat_{Guid.NewGuid():N}.txt");
            var concatLines = tempFiles.Select(f => $"file '{Path.GetFileName(f)}'");
            File.WriteAllLines(concatFilePath, concatLines);
            tempFiles.Add(concatFilePath);

            var concatConversion = FFmpeg.Conversions.New()
                .AddParameter("-f concat")
                .AddParameter("-safe 0")
                .AddParameter($"-i \"{concatFilePath}\"")
                .AddParameter("-c:v copy")
                .AddParameter(r_options.EncodingAudio);

            StartAndHandleFfmpegProgress(concatConversion, outputVideo);

            WriteInfo("Variable speed video generation complete!");

            // Auto-Sync Assets after render finishes
            SyncSidecarAssets(inputVideo, outputVideo, offsetsJsonFile);

            foreach (var f in tempFiles)
            {
                try { File.Delete(f); } catch { /* Ignore cleanup errors to allow full sweep */ }
            }
        }

        private List<SpeedSegment> GenerateSpeedSegments(List<Subtitle> subtitles, TimeSpan videoDuration, double videoFramerate)
        {
            // 1. Parse subtitles into controlled blocks
            var controlledBlocks = new List<ControlBlock>();
            foreach (var sub in subtitles)
            {
                double speed = r_options.DefaultSpeedControlled;

                var match = rs_speedRegex.Match(sub.Text);
                if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsedSpeed))
                {
                    speed = parsedSpeed;
                }

                controlledBlocks.Add(new ControlBlock
                {
                    StartTime = sub.StartTime,
                    EndTime = sub.EndTime,
                    Speed = speed,
                    OriginalDuration = sub.Duration
                });
            }

            // 2. Sweep the timeline to resolve overlaps (Longest Original Duration wins)
            var rawSegments = BuildRawTimeline(controlledBlocks, videoDuration);

            // 3. Eliminate Micro-Gaps & calculate Uncontrolled speeds
            var processedSegments = ProcessGapsAndUncontrolledSpeeds(rawSegments);

            // 4. Merge identical contiguous rules and snap to frames using the external SpeedSegment class
            return MergeAndFrameAlignSegments(processedSegments, videoFramerate);
        }

        private List<RawSegment> BuildRawTimeline(List<ControlBlock> blocks, TimeSpan videoDuration)
        {
            var events = new List<TimeEvent>();
            foreach (var block in blocks)
            {
                events.Add(new TimeEvent { Time = block.StartTime, IsStart = true, Block = block });
                events.Add(new TimeEvent { Time = block.EndTime, IsStart = false, Block = block });
            }

            events = events.OrderBy(e => e.Time).ThenBy(e => e.IsStart ? 1 : 0).ToList();

            var rawSegments = new List<RawSegment>();
            var activeBlocks = new HashSet<ControlBlock>();
            TimeSpan currentTime = TimeSpan.Zero;

            foreach (var ev in events)
            {
                if (ev.Time > currentTime)
                {
                    var winningBlock = activeBlocks.OrderByDescending(b => b.OriginalDuration).FirstOrDefault();

                    rawSegments.Add(new RawSegment
                    {
                        StartTime = currentTime,
                        EndTime = ev.Time,
                        IsControlled = winningBlock != null,
                        Speed = winningBlock?.Speed ?? r_options.DefaultSpeedUncontrolled
                    });

                    currentTime = ev.Time;
                }

                if (ev.IsStart) activeBlocks.Add(ev.Block);
                else activeBlocks.Remove(ev.Block);
            }

            if (currentTime < videoDuration)
            {
                rawSegments.Add(new RawSegment
                {
                    StartTime = currentTime,
                    EndTime = videoDuration,
                    IsControlled = false,
                    Speed = r_options.DefaultSpeedUncontrolled
                });
            }

            return rawSegments;
        }

        private List<RawSegment> ProcessGapsAndUncontrolledSpeeds(List<RawSegment> segments)
        {
            var results = new List<RawSegment>();

            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];

                if (!seg.IsControlled)
                {
                    if (seg.Duration.TotalSeconds <= r_options.MicroGapThreshold)
                    {
                        seg.IsControlled = true;

                        if (results.Count > 0) seg.Speed = results.Last().Speed;
                        else if (i + 1 < segments.Count) seg.Speed = segments[i + 1].Speed;
                        else seg.Speed = r_options.DefaultSpeedControlled;
                    }
                    else
                    {
                        double minSpeedRequired = seg.Duration.TotalSeconds / r_options.MaxUncontrolledDuration;
                        seg.Speed = Math.Max(r_options.DefaultSpeedUncontrolled, minSpeedRequired);
                    }
                }
                results.Add(seg);
            }

            return results;
        }

        private List<SpeedSegment> MergeAndFrameAlignSegments(List<RawSegment> rawSegments, double videoFramerate)
        {
            var finalSegments = new List<SpeedSegment>();
            SpeedSegment activeSegment = null;
            double frameDuration = 1.0 / videoFramerate;

            foreach (var raw in rawSegments)
            {
                if (raw.Duration.TotalSeconds <= 0) continue;

                if (activeSegment == null)
                {
                    activeSegment = new SpeedSegment { StartTime = raw.StartTime, EndTime = raw.EndTime, Speed = raw.Speed };
                }
                else if (Math.Abs(activeSegment.Speed - raw.Speed) < 0.001) // Same rule, merge
                {
                    activeSegment.EndTime = raw.EndTime;
                }
                else
                {
                    // Speed changing! Snap boundary to exact frame to prevent FFmpeg drift
                    double boundarySecs = activeSegment.EndTime.TotalSeconds;
                    double frames = Math.Round(boundarySecs / frameDuration);
                    TimeSpan snappedBoundary = TimeSpan.FromSeconds(frames * frameDuration);

                    activeSegment.EndTime = snappedBoundary;
                    finalSegments.Add(activeSegment);

                    activeSegment = new SpeedSegment { StartTime = snappedBoundary, EndTime = raw.EndTime, Speed = raw.Speed };
                }
            }

            if (activeSegment != null)
                finalSegments.Add(activeSegment);

            return finalSegments;
        }

        private string GetAudioTempoFilter(double speed)
        {
            if (speed <= 0) return "atempo=1.0";

            var filters = new List<string>();
            double currentSpeed = speed;

            // FFmpeg atempo only supports values between 0.5 and 100.0. We chain them if necessary.
            while (currentSpeed > 100.0)
            {
                filters.Add("atempo=100.0");
                currentSpeed /= 100.0;
            }
            while (currentSpeed < 0.5 && currentSpeed > 0)
            {
                filters.Add("atempo=0.5");
                currentSpeed /= 0.5;
            }

            filters.Add($"atempo={currentSpeed.ToString(CultureInfo.InvariantCulture)}");
            return string.Join(",", filters);
        }

        // --- Helper Models only used internally for timeline mapping ---

        private class ControlBlock
        {
            public TimeSpan StartTime { get; set; }
            public TimeSpan EndTime { get; set; }
            public double Speed { get; set; }
            public TimeSpan OriginalDuration { get; set; }
        }

        private class TimeEvent
        {
            public TimeSpan Time { get; set; }
            public bool IsStart { get; set; }
            public ControlBlock Block { get; set; }
        }

        private class RawSegment
        {
            public TimeSpan StartTime { get; set; }
            public TimeSpan EndTime { get; set; }
            public double Speed { get; set; }
            public bool IsControlled { get; set; }
            public TimeSpan Duration => EndTime - StartTime;
        }
    }
}