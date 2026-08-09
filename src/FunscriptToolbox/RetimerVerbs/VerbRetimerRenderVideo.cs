using CommandLine;
using FunscriptToolbox.Core;
using log4net;
using Newtonsoft.Json;
using System.Collections.Generic;
using System;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using FunscriptToolbox.Core.Infra;

namespace FunscriptToolbox.RetimerVerbs
{
    [JsonObject(IsReference = false)]
    class VerbRetimerRenderVideo : Verb
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("retimer.rendervideo", aliases: new[] { "retimer.render" }, HelpText = "Create a 'story only' video from a video.")]
        public class Options : OptionsBase
        {
            [Option("subtitles", Required = true)]
            public string SubtitleFile { get; set; }

            [Option("video", Required = true)]
            public string Video { get; set; }
        }

        private readonly Options r_options;

        public VerbRetimerRenderVideo(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            ProcessVideoWithSubtitleSpeed(
                r_options.SubtitleFile,
                r_options.Video,
                r_options.Video.Replace(".mp4", "-STORY.mp4"));
            return 0;
        }

        public void ProcessVideoWithSubtitleSpeed(
            string subtitleFilePath,
            string videoFilePath,
            string outputFilePath,
            decimal bufferBeforeSubtitle = 0.5M,
            decimal bufferAfterSubtitle = 0.5M,
            decimal fastSpeed = 10.0M)
        {
            // 1. Load subtitles
            var subtitleFile = SubtitleFile.FromSrtFile(subtitleFilePath);

            // 2. Dynamically probe exact video framerate and duration
            var (fpsString, fpsDouble) = GetExactFrameRate(videoFilePath);
            var videoDuration = GetVideoDuration(videoFilePath);

            WriteInfo($"Video loaded. Duration: {videoDuration}, Framerate: {fpsString} ({fpsDouble} fps)");

            // 3. Generate logical speed segments (Time-based mapping)
            var segments = GenerateSpeedSegments(
                subtitleFile.Subtitles,
                videoDuration,
                (decimal)fpsDouble,
                bufferBeforeSubtitle,
                bufferAfterSubtitle,
                fastSpeed,
                8);

            // 4. Process video segments perfectly synchronizing via CFR, and output concatenated file + offsets
            ProcessVideoPerfectSync(videoFilePath, outputFilePath, segments, fpsString, fpsDouble);
        }

        private void ProcessVideoPerfectSync(
            string inputVideo,
            string outputVideo,
            List<SpeedSegment> segments,
            string fpsString,
            double fpsDouble)
        {
            var offsets = new List<SyncOffsetDto>();
            TimeSpan currentOutputTime = TimeSpan.Zero;
            List<string> tempFiles = new List<string>();

            WriteInfo($"Starting empirical segment extraction for {segments.Count} segments...");
            WriteInfo($"Estimated final duration: {segments.Sum(f => f.EstimatedFinalDuration)}");

            var guid = Guid.NewGuid();
            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                var segmentDuration = (segment.EndTime - segment.StartTime).TotalSeconds;

                if (segmentDuration <= 0) continue;

                string tempFile = $"segment_{guid:N}_{i:D3}.mp4";
                tempFiles.Add(tempFile);

                double speedFactor = 1.0;
                double videoPtsFactor = 1.0;
                double audioSpeedFactor = 1.0;

                // Determine speed multipliers matching original logic
                switch (segment.SpeedType)
                {
                    case SpeedType.Normal:
                        speedFactor = 1.0;
                        videoPtsFactor = 1.0;
                        audioSpeedFactor = 1.0;
                        break;
                    case SpeedType.Fast:
                        speedFactor = (double)segment.Speed;
                        videoPtsFactor = 1.0 / speedFactor;
                        audioSpeedFactor = speedFactor;
                        break;
                }

                // Force InvariantCulture to avoid comma/dot issues
                string vPtsStr = videoPtsFactor.ToString(CultureInfo.InvariantCulture);
                string aSpdStr = audioSpeedFactor.ToString(CultureInfo.InvariantCulture);
                string startStr = segment.StartTime.TotalSeconds.ToString(CultureInfo.InvariantCulture);

                // Use duration (-t) instead of end time (-to) when using input seeking (-ss)
                string durationStr = segmentDuration.ToString(CultureInfo.InvariantCulture);

                // Build strict CFR intra-frame filter
                // Removed 'trim' and 'atrim' since the input -ss and -t parameters already do the cutting.
                // Using (PTS-STARTPTS) ensures the timestamps start perfectly at 0 for the math.
                string vFilter = $"[0:v]setpts={vPtsStr}*(PTS-STARTPTS),fps={fpsString}[v]";
                string aFilter = $"[0:a]asetpts=PTS-STARTPTS,atempo={aSpdStr}[a]";

                // -ss and -t placed BEFORE -i act as fast, frame-accurate input trimmers during a transcode
                string cmd = $"-ss {startStr} -t {durationStr} -i \"{inputVideo}\" -filter_complex \"{vFilter};{aFilter}\" -map \"[v]\" -map \"[a]\" -c:v libx265 -crf 20 -tag:v hvc1 -c:a aac -avoid_negative_ts make_zero -video_track_times_scale 90000 -map_metadata -1 -y \"{tempFile}\"";

                WriteInfo($"Encoding Segment {i + 1}/{segments.Count} [{segment.SpeedType}], {segment.StartTime} to {segment.EndTime}...");
                RunProcess("ffmpeg", cmd);

                // Probe exact frame count of generated segment to eliminate mathematical drift
                int exactFrames = GetExactFrameCount(tempFile);
                TimeSpan actualOutputDuration = TimeSpan.FromSeconds(exactFrames / fpsDouble);

                // Record the actual offset based on reality
                var offset = new SyncOffsetDto
                {
                    InputStartTime = segment.StartTime,
                    OutputStartTime = currentOutputTime,
                    Duration = segment.EndTime - segment.StartTime,
                    Offset = currentOutputTime - segment.StartTime
                };
                offsets.Add(offset);

                // Advance output timeline by the EXACT duration measured
                currentOutputTime += actualOutputDuration;
            }

            // Concatenate all segments seamlessly
            WriteInfo("Concatenating segments...");
            string concatFilePath = $"concat_{Guid.NewGuid():N}.txt";
            File.WriteAllLines(concatFilePath, tempFiles.Select(f => $"file '{f}'"));

            string concatCmd = $"-f concat -safe 0 -i \"{concatFilePath}\" -c copy -y \"{outputVideo}\"";
            RunProcess("ffmpeg", concatCmd);

            // Write out the Offsets mapping file exactly matching the structure of VirtualMergedAudioOffset
            string offsetsJsonFile = outputVideo.Replace(".mp4", ".offsets.json");
            File.WriteAllText(
                offsetsJsonFile,
                JsonConvert.SerializeObject(
                    offsets,
                    Formatting.Indented,
                    new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore
                    }));
            WriteInfo($"Wrote synchronization offsets to: {offsetsJsonFile}");

            // Cleanup temp files
            foreach (var f in tempFiles) { if (File.Exists(f)) File.Delete(f); }
            if (File.Exists(concatFilePath)) File.Delete(concatFilePath);

            WriteInfo("Story mode video generation complete!");
        }

        private (string FpsString, double FpsDouble) GetExactFrameRate(string videoPath)
        {
            // e.g. Output might be "30000/1001" or "25/1" or "60000/1001"
            string cmd = $"-v error -select_streams v:0 -show_entries stream=r_frame_rate -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"";
            string output = RunProcessAndReadOutput("ffprobe", cmd);

            string fpsStr = output.Trim();
            if (string.IsNullOrEmpty(fpsStr))
                throw new Exception("Could not determine framerate from video.");

            var parts = fpsStr.Split('/');
            double fpsDouble = 0;
            if (parts.Length == 2 && double.TryParse(parts[0], out double num) && double.TryParse(parts[1], out double den))
            {
                fpsDouble = num / den;
            }
            else if (double.TryParse(fpsStr, out double exact))
            {
                fpsDouble = exact;
            }
            else
            {
                throw new Exception($"Failed to parse framerate string: {fpsStr}");
            }

            return (fpsStr, fpsDouble);
        }

        private int GetExactFrameCount(string filePath)
        {
            string cmd = $"-v error -select_streams v:0 -show_entries stream=nb_frames -of default=nokey=1:noprint_wrappers=1 \"{filePath}\"";
            string output = RunProcessAndReadOutput("ffprobe", cmd);

            if (int.TryParse(output.Trim(), out int frames))
                return frames;

            throw new Exception($"Could not read frame count from generated segment: {filePath}");
        }

        private TimeSpan GetVideoDuration(string videoPath)
        {
            string cmd = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"";
            string output = RunProcessAndReadOutput("ffprobe", cmd);

            if (double.TryParse(output.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double durationSecs))
                return TimeSpan.FromSeconds(durationSecs);

            throw new Exception("Could not determine video duration.");
        }

        private void RunProcess(string fileName, string arguments)
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            p.Start();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                throw new Exception($"Process '{fileName}' exited with code {p.ExitCode}. Args: {arguments}");
            }
        }

        private string RunProcessAndReadOutput(string fileName, string arguments)
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            p.Start();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                throw new Exception($"Process '{fileName}' exited with code {p.ExitCode}. Args: {arguments}");
            }
            return output;
        }

        private List<SpeedSegment> GenerateSpeedSegments(
            List<Subtitle> subtitles,
            TimeSpan videoDuration,
            decimal videoFramerate,
            decimal bufferBefore,
            decimal bufferAfter,
            decimal fastSpeed,
            double minimalGapThreshold)
        {
            // 1. Calculate buffered subtitle blocks and MERGE OVERLAPS
            // (This must happen first to figure out the true layout of the timeline)
            var activeBlocks = new List<Tuple<TimeSpan, TimeSpan>>();
            var sortedSubtitles = subtitles.OrderBy(s => s.StartTime).ToList();

            foreach (var sub in sortedSubtitles)
            {
                var start = sub.StartTime - TimeSpan.FromSeconds((double)bufferBefore);
                var end = sub.EndTime + TimeSpan.FromSeconds((double)bufferAfter);

                if (start < TimeSpan.Zero) start = TimeSpan.Zero;
                if (end > videoDuration) end = videoDuration;

                if (!activeBlocks.Any())
                {
                    activeBlocks.Add(Tuple.Create(start, end));
                }
                else
                {
                    var lastBlock = activeBlocks.Last();
                    if (start <= lastBlock.Item2)
                    {
                        var newEnd = end > lastBlock.Item2 ? end : lastBlock.Item2;
                        activeBlocks[activeBlocks.Count - 1] = Tuple.Create(lastBlock.Item1, newEnd);
                    }
                    else
                    {
                        activeBlocks.Add(Tuple.Create(start, end));
                    }
                }
            }

            // 2. State Machine: Generate, Merge, and Frame-Align in a single pass
            var finalSegments = new List<SpeedSegment>();
            SpeedSegment activeSegment = null;
            decimal frameDuration = 1.0M / videoFramerate;

            // Local helper to handle the active segment state
            void AddOrExtendSegment(TimeSpan targetEnd, SpeedType speedType, decimal speed)
            {
                if (targetEnd <= (activeSegment?.EndTime ?? TimeSpan.Zero))
                    return; // Skip 0-length additions

                if (activeSegment == null)
                {
                    // First segment
                    activeSegment = new SpeedSegment
                    {
                        StartTime = TimeSpan.Zero,
                        EndTime = targetEnd,
                        SpeedType = speedType,
                        Speed = speed
                    };
                }
                else if (activeSegment.SpeedType == speedType && activeSegment.Speed == speed)
                {
                    // MERGE: Same speed, so just stretch the current segment's end time
                    activeSegment.EndTime = targetEnd;
                }
                else
                {
                    // ALIGN: Speed is changing! Snap the boundary to the nearest frame
                    decimal boundarySecs = (decimal)activeSegment.EndTime.TotalSeconds;
                    decimal frames = Math.Round(boundarySecs / frameDuration);
                    TimeSpan snappedBoundary = TimeSpan.FromSeconds((double)(frames * frameDuration));

                    activeSegment.EndTime = snappedBoundary;
                    finalSegments.Add(activeSegment);

                    // Start the next segment from the exact snapped boundary
                    activeSegment = new SpeedSegment
                    {
                        StartTime = snappedBoundary,
                        EndTime = targetEnd,
                        SpeedType = speedType,
                        Speed = speed
                    };
                }
            }

            // 3. Process the timeline
            TimeSpan currentTime = TimeSpan.Zero;

            foreach (var block in activeBlocks)
            {
                var blockStart = block.Item1;
                var blockEnd = block.Item2;

                if (currentTime < blockStart)
                {
                    var gapDuration = (blockStart - currentTime).TotalSeconds;
                    if (gapDuration > minimalGapThreshold)
                        AddOrExtendSegment(blockStart, SpeedType.Fast, fastSpeed);
                    else
                        AddOrExtendSegment(blockStart, SpeedType.Normal, 1.0M);
                }

                AddOrExtendSegment(blockEnd, SpeedType.Normal, 1.0M);
                currentTime = blockEnd;
            }

            // Handle remaining video tail
            if (currentTime < videoDuration)
            {
                var remainingDuration = (videoDuration - currentTime).TotalSeconds;
                if (remainingDuration > minimalGapThreshold)
                    AddOrExtendSegment(videoDuration, SpeedType.Fast, fastSpeed);
                else
                    AddOrExtendSegment(videoDuration, SpeedType.Normal, 1.0M);
            }

            // Add the final segment and lock it to the end of the video
            if (activeSegment != null)
            {
                activeSegment.EndTime = videoDuration;
                finalSegments.Add(activeSegment);
            }

            return finalSegments;
        }

        private class SpeedSegment
        {
            public TimeSpan StartTime { get; set; }
            public TimeSpan EndTime { get; set; }
            public SpeedType SpeedType { get; set; }
            public decimal Speed { get; set; }
            public TimeSpan Duration => this.EndTime - this.StartTime;
            public TimeSpan EstimatedFinalDuration => TimeSpan.FromMilliseconds(this.Duration.TotalMilliseconds / (double)this.Speed);

            public override string ToString()
            {
                return $"Source: [{StartTime:g}-{EndTime:g}], Type: {SpeedType}, Speed: {Speed}";
            }

            internal void AdjustTime(decimal videoFramerate)
            {
                var x = 1M / videoFramerate;
                var frameStartTime = Math.Ceiling((decimal)this.StartTime.TotalSeconds / x);
                this.StartTime = TimeSpan.FromSeconds((double)(frameStartTime * x));
                var frameEndTime = Math.Ceiling((decimal)this.EndTime.TotalSeconds / x);
                this.EndTime = TimeSpan.FromSeconds((double)(frameEndTime * x));
            }
        }

        private enum SpeedType
        {
            Normal,
            Fast
        }

        // This inner class is designed to perfectly mimic the structure of your VirtualMergedAudioOffset
        // for serialization out to the offsets.json file without circular dependencies.
        private class SyncOffsetDto
        {
            [JsonProperty("InputFile")]
            public string InputFilePath => null; // You indicated this was naturally null in your scenario for singular files

            public TimeSpan? InputStartTime { get; set; }

            [JsonProperty("OutputFile")]
            public string OutputFilePath => null;

            public TimeSpan? OutputStartTime { get; set; }
            public TimeSpan Duration { get; set; }
            public TimeSpan? Offset { get; set; }

            public Dictionary<string, int> Usage { get; } = new Dictionary<string, int>
            {
                { "Actions", 0 },
                { "Chapters", 0 },
                { "Subtitles", 0 }
            };
        }
    }
}