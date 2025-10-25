using CommandLine;
using FunscriptToolbox.Core;
using log4net;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Text;
using System;
using Xabe.FFmpeg;
using System.Linq;
using System.IO;
using FunscriptToolbox.Core.Infra;

namespace FunscriptToolbox.SubtitlesVerbs
{
    [JsonObject(IsReference = false)]
    class VerbSubtitlesStoryVideo : Verb
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("subtitles.storyvideo", aliases: new[] { "sub.sv" }, HelpText = "Create a 'story only' video from a video.")]
        public class Options : OptionsBase
        {
            [Option("subtitles", Required = true)]
            public string SubtitleFile { get; set; }

            [Option("video", Required = true)]
            public string Video { get; set; }
        }

        private readonly Options r_options;

        public VerbSubtitlesStoryVideo(Options options)
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

        private const double TRANSITION_DURATION = 3.0; // seconds for speed ramp up/down

        public void ProcessVideoWithSubtitleSpeed(
            string subtitleFilePath,
            string videoFilePath,
            string outputFilePath,
            decimal bufferBeforeSubtitle = 0.0M,
            decimal bufferAfterSubtitle = 0.0M,
            decimal fastSpeed = 10.0M)
        {
            // Load subtitles
            var subtitleFile = SubtitleFile.FromSrtFile(subtitleFilePath);

            // Get video info
            var mediaInfo = FFmpeg.GetMediaInfo(videoFilePath).Result;
            var videoDuration = mediaInfo.Duration;
            // Use the actual framerate from the video file for precision
            var videoFramerate = (decimal)mediaInfo.VideoStreams.First().Framerate;

            // Generate speed segments
            var segments = GenerateSpeedSegments(
                subtitleFile.Subtitles,
                videoDuration,
                videoFramerate,
                bufferBeforeSubtitle,
                bufferAfterSubtitle,
                fastSpeed);

            var numberFramesStart = (int)(videoDuration.TotalSeconds * (double)videoFramerate);
            var numberFramesFinal = (int)(segments.Last().FinalEndTime.TotalSeconds * (double)videoFramerate);
            var numberFramesIGot = (int)(new TimeSpan(0, 0, 19, 10, 515).TotalSeconds * (double)videoFramerate);

            var split = segments.GroupBy(f => f.SpeedType).ToDictionary(item => item.Key, item => item.ToArray());
            var xxx = split.Select(item => new { SpeedType = item.Key, NbFrames = item.Value.Sum(k => (int)((double)k.Duration * (double)videoFramerate)) });
            var xxxFinal = split.Select(item => new { SpeedType = item.Key, NbFrames = item.Value.Sum(k => (int)((double)k.FinalDuration * (double)videoFramerate)) });

            var diff = numberFramesIGot - numberFramesFinal;
            var x = 0;
            // Build FFmpeg command with complex filter
            var ffmpegCommand = BuildFFmpegCommand(
                videoFilePath,
                outputFilePath,
                segments);

            // Execute FFmpeg
            ExecuteFFmpegCommand(ffmpegCommand, outputFilePath);
        }

        private List<SpeedSegment> GenerateSpeedSegments(
            List<Subtitle> subtitles,
            TimeSpan videoDuration,
            decimal videoFramerate,
            decimal bufferBefore,
            decimal bufferAfter,
            decimal fastSpeed)
        {
            var segments = new List<SpeedSegment>();
            var sortedSubtitles = subtitles.OrderBy(s => s.StartTime).ToList();

            TimeSpan currentTime = TimeSpan.Zero;

            // region Segment Generation (Time-based)
            // First, generate the segments based on subtitle timings.
            for (int i = 0; i < sortedSubtitles.Count; i++)
            {
                var subtitle = sortedSubtitles[i];
                var subtitleStart = subtitle.StartTime - TimeSpan.FromSeconds((double)bufferBefore);
                var subtitleEnd = subtitle.EndTime + TimeSpan.FromSeconds((double)bufferAfter);

                if (subtitleStart < TimeSpan.Zero) subtitleStart = TimeSpan.Zero;
                if (subtitleEnd > videoDuration) subtitleEnd = videoDuration;

                if (currentTime < subtitleStart)
                {
                    var gapDuration = (subtitleStart - currentTime).TotalSeconds;
                    if (gapDuration > TRANSITION_DURATION * 2 + 5.0) // TODO Receive minimal gap in parameter
                    {
                        // Add transition to fast speed
                        segments.Add(new SpeedSegment
                        {
                            StartTime = currentTime,
                            EndTime = currentTime + TimeSpan.FromSeconds(TRANSITION_DURATION),
                            SpeedType = SpeedType.NormalToFast,
                            Speed = fastSpeed
                        });

                        // Add fast speed segment
                        segments.Add(new SpeedSegment
                        {
                            StartTime = currentTime + TimeSpan.FromSeconds(TRANSITION_DURATION),
                            EndTime = subtitleStart - TimeSpan.FromSeconds(TRANSITION_DURATION),
                            SpeedType = SpeedType.Fast,
                            Speed = fastSpeed
                        });

                        // Add transition back to normal
                        segments.Add(new SpeedSegment
                        {
                            StartTime = subtitleStart - TimeSpan.FromSeconds(TRANSITION_DURATION),
                            EndTime = subtitleStart,
                            SpeedType = SpeedType.FastToNormal,
                            Speed = fastSpeed
                        });
                    }
                    else
                    {
                        // Gap too small for transitions, keep normal speed
                        segments.Add(new SpeedSegment
                        {
                            StartTime = currentTime,
                            EndTime = subtitleStart,
                            SpeedType = SpeedType.Normal,
                            Speed = 1.0M
                        });
                    }
                }

                // Add normal speed segment for subtitle
                segments.Add(new SpeedSegment
                {
                    StartTime = subtitleStart,
                    EndTime = subtitleEnd,
                    SpeedType = SpeedType.Normal,
                    Speed = 1.0M
                });
                currentTime = subtitleEnd;
            }

            // Handle remaining video after last subtitle
            if (currentTime < videoDuration)
            {
                var remainingDuration = (videoDuration - currentTime).TotalSeconds;
                if (remainingDuration > TRANSITION_DURATION) // Minimal
                {
                    segments.Add(new SpeedSegment
                    {
                        StartTime = currentTime,
                        EndTime = currentTime + TimeSpan.FromSeconds(TRANSITION_DURATION),
                        SpeedType = SpeedType.NormalToFast,
                        Speed = fastSpeed
                    });
                    segments.Add(new SpeedSegment
                    {
                        StartTime = currentTime + TimeSpan.FromSeconds(TRANSITION_DURATION),
                        EndTime = videoDuration,
                        SpeedType = SpeedType.Fast,
                        Speed = fastSpeed
                    });
                }
                else
                {
                    segments.Add(new SpeedSegment
                    {
                        StartTime = currentTime,
                        EndTime = videoDuration,
                        SpeedType = SpeedType.Normal,
                        Speed = 1.0M
                    });
                }
            }

            var mergedSegments = MergeAdjacentSegments(segments);

            foreach (var segment in mergedSegments)
            {
                segment.AdjustTime(videoFramerate);
            }

            // region Frame-based Timeline Calculation
            // 1. Create an array representing the exact timestamp of every frame in the source video.
            var totalFrames = (int)(videoDuration.TotalSeconds * (double)videoFramerate);
            var frameDurationSeconds = 1.0m / videoFramerate;
            var originalFrameTimestamps = Enumerable.Range(0, totalFrames)
                                                    .Select(f => (decimal)f * frameDurationSeconds)
                                                    .ToArray();

            TimeSpan currentFinalTime = TimeSpan.Zero;

            // 2. Distribute frames into segments and calculate the final timeline.
            foreach (var segment in mergedSegments)
            {
                // Find all frames from the source video that belong to this segment.
                var segmentFrames = originalFrameTimestamps
                    .Where(t => t >= (decimal)segment.StartTime.TotalSeconds && t < (decimal)segment.EndTime.TotalSeconds)
                    .ToArray();

                if (segmentFrames.Length == 0) continue;

                decimal segmentOutputDurationSeconds = 0;

                // Calculate the output duration of this segment based on its frames.
                switch (segment.SpeedType)
                {
                    case SpeedType.Normal:
                        segmentOutputDurationSeconds = segmentFrames.Length * frameDurationSeconds;
                        break;

                    case SpeedType.Fast:
                        segmentOutputDurationSeconds = (segmentFrames.Length * frameDurationSeconds) / segment.Speed;
                        break;

                    case SpeedType.NormalToFast:
                    case SpeedType.FastToNormal:
                        // For transitions, calculate the new duration of each individual frame as the speed changes.
                        var startSpeed = (segment.SpeedType == SpeedType.NormalToFast) ? (decimal)1.0 : segment.Speed;
                        var endSpeed = (segment.SpeedType == SpeedType.NormalToFast) ? segment.Speed : (decimal)1.0;

                        var totalTransitionDuration = segment.Duration;
                        var segmentStartTime = (decimal)segment.StartTime.TotalSeconds;

                        foreach (var frameTimestamp in segmentFrames)
                        {
                            // How far are we into the transition (0.0 to 1.0)?
                            var progress = (frameTimestamp - segmentStartTime) / totalTransitionDuration;
                            // Linearly interpolate the speed at this specific frame's timestamp.
                            var instantaneousSpeed = startSpeed + progress * (endSpeed - startSpeed);
                            // The output duration of this single frame is its original duration divided by its new speed.
                            segmentOutputDurationSeconds += frameDurationSeconds / instantaneousSpeed;
                        }
                        break;
                }

                // 3. Update the segment's final start and end times in the output video.
                segment.FinalStartTime = currentFinalTime;
                segment.FinalEndTime = currentFinalTime + TimeSpan.FromSeconds((double)segmentOutputDurationSeconds);
                currentFinalTime = segment.FinalEndTime;
            }
            // endregion

            return mergedSegments;
        }

        private List<SpeedSegment> MergeAdjacentSegments(List<SpeedSegment> segments)
        {
            if (!segments.Any()) return new List<SpeedSegment>();

            var merged = new List<SpeedSegment> { segments.First() };

            foreach (var currentSegment in segments.Skip(1))
            {
                var lastSegment = merged.Last();
                if (lastSegment.SpeedType == currentSegment.SpeedType && lastSegment.Speed.Equals(currentSegment.Speed) && lastSegment.EndTime == currentSegment.StartTime)
                {
                    lastSegment.EndTime = currentSegment.EndTime;
                }
                else
                {
                    merged.Add(currentSegment);
                }
            }
            return merged;
        }

        private string BuildFFmpegCommand(
            string inputVideo,
            string outputVideo,
            List<SpeedSegment> segments)
        {
            var filterBuilder = new StringBuilder();
            var concatBuilder = new StringBuilder();

            // Split video into segments and apply speed changes
            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                var segmentFilter = new StringBuilder();
                var segmentDuration = (segment.EndTime - segment.StartTime).TotalSeconds;

                // Ensure segment duration is not zero to avoid division by zero errors
                if (segmentDuration <= 0)
                {
                    continue;
                }

                // Trim segment
                segmentFilter.Append($"[0:v]trim=start={segment.StartTime.TotalSeconds}:end={segment.EndTime.TotalSeconds},setpts=PTS-STARTPTS");

                // Apply speed filter based on type
                switch (segment.SpeedType)
                {
                    case SpeedType.Normal:
                        // No speed change needed
                        segmentFilter.Append(",drawbox=w=iw/5:h=ih:color=yellow:t=fill"); // Also colorize transitions
                        break;

                    case SpeedType.Fast:
                        segmentFilter.Append($",setpts={1.0 / (double)segment.Speed}*PTS");
                        segmentFilter.Append(",drawbox=w=iw/5:h=ih:color=red:t=fill"); // Also colorize transitions
                        break;

                    case SpeedType.FastToNormal:
                    case SpeedType.NormalToFast:
                        segmentFilter.Append($",setpts={1.0 / (double)(segment.Speed/2)}*PTS");
                        segmentFilter.Append(",drawbox=w=iw/5:h=ih:color=green:t=fill"); // Also colorize transitions
                        break;
                }

                segmentFilter.Append($"[v{i}];");

                // Audio processing
                segmentFilter.Append($"[0:a]atrim=start={segment.StartTime.TotalSeconds}:end={segment.EndTime.TotalSeconds},asetpts=PTS-STARTPTS");

                switch (segment.SpeedType)
                {
                    case SpeedType.Normal:
                        // No speed change
                        break;

                    case SpeedType.Fast:
                        segmentFilter.Append($",atempo={(double)segment.Speed}");
                        break;

                    case SpeedType.NormalToFast:
                    case SpeedType.FastToNormal:
                        // For transitions, using an average speed for audio is a reasonable approach
                        var avgSpeed = (1.0 + (double)segment.Speed) / 2.0;
                        segmentFilter.Append($",atempo={avgSpeed}");
                        break;
                }

                segmentFilter.Append($"[a{i}];");

                filterBuilder.AppendLine(segmentFilter.ToString());
                concatBuilder.Append($"[v{i}][a{i}]");
            }

            // Concatenate all segments
            filterBuilder.Append($"{concatBuilder}concat=n={segments.Count}:v=1:a=1[outv][outa]");

            // Create a temporary file to store the filtergraph
            string filterScriptPath = Path.Combine(Path.GetTempPath(), "ffmpeg_filter_script.txt");
            File.WriteAllText(filterScriptPath, filterBuilder.ToString());
            File.WriteAllText($"{outputVideo}.txt", filterBuilder.ToString());

            // Build the FFmpeg command using -filter_complex_script
            var command = $"-i \"{inputVideo}\" -filter_complex_script \"{filterScriptPath}\" -map \"[outv]\" -map \"[outa]\" -c:v libx264 -c:a aac -y \"{outputVideo}\"";

            return command;
        }

        private void ExecuteFFmpegCommand(string command, string outputFile)
        {
            Console.WriteLine("Executing FFmpeg with command:");
            Console.WriteLine($"ffmpeg {command}");

            // The Xabe.FFmpeg library builds the command internally.
            // We pass the parameters without "ffmpeg" at the start.
            var conversion = FFmpeg.Conversions.New();
            foreach (var arg in command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                conversion.AddParameter(arg);
            }

            // It seems your StartAndHandleFfmpegProgress is a custom method.
            // I'll substitute a standard execution call.
            // StartAndHandleFfmpegProgress(conversion, outputFile);
            conversion.SetOverwriteOutput(true).Start().Wait();
        }

        private class SpeedSegment
        {
            public TimeSpan StartTime { get; set; }
            public TimeSpan EndTime { get; set; }
            public SpeedType SpeedType { get; set; }
            public decimal Speed { get; set; }

            public decimal Duration => (decimal)(EndTime - StartTime).TotalSeconds;

            public TimeSpan FinalStartTime { get; set; }
            public TimeSpan FinalEndTime { get; set; }
            public decimal FinalDuration => (decimal)(FinalEndTime - FinalStartTime).TotalSeconds;

            public override string ToString()
            {
                return $"Source: [{StartTime:g}-{EndTime:g}], Final: [{FinalStartTime:g}-{FinalEndTime:g}], Type: {SpeedType}, Speed: {Speed}";
            }

            internal void AdjustTime(decimal videoFramerate)
            {
                var x = 1M / videoFramerate;
                var frameStartTime = Math.Ceiling((decimal)this.StartTime.TotalSeconds / x);
                this.StartTime = TimeSpan.FromSeconds((double)(frameStartTime * x + x / 2));
                var frameEndTime = Math.Ceiling((decimal)this.EndTime.TotalSeconds / x);
                this.EndTime = TimeSpan.FromSeconds((double)(frameEndTime * x + x / 2));
            }
        }

        private enum SpeedType
        {
            Normal,
            Fast,
            NormalToFast,
            FastToNormal
        }
    }
}