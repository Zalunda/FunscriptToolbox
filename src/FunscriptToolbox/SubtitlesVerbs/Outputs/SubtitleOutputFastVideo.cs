using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Progress;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    /// <summary>
    /// A light video of each part, next to it ("video.mp4" => "video.FAST.mp4"): small and quick to seek (all key frames),
    /// with a clear audio. Used by the Timing editor of the web UI and the speaker correction (they only look for '.FAST.mp4').
    /// Made with ffmpeg ('FfmpegParameters': what is between the input and the output), one part after the other: the lock 'ENC' is kept
    /// for all the parts of the scene (the parts of several scenes are not mixed: each scene gets all its light videos before the next one).
    /// The file is written under a temporary name, then renamed: a file that is there is complete.
    /// </summary>
    internal class SubtitleOutputFastVideo : SubtitleOutput
    {
        /// <summary>Replaces the extension of the video (ex. ".FAST.mp4"). The other tools only look for '.FAST.mp4'.</summary>
        [JsonProperty(Order = 5, Required = Required.Always)]
        public string FileSuffix { get; set; }

        /// <summary>The ffmpeg parameters between the input (-i) and the output, ex. the filters and the codecs.</summary>
        [JsonProperty(Order = 10, Required = Required.Always)]
        public string FfmpegParameters { get; set; }

        protected override void DescribeDataFlow(WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            flow.Input(DataKind.Video, "video", "source");
            flow.Output(DataKind.File, this.FileSuffix);
        }

        protected override bool IsPrerequisitesMet(SubtitleGeneratorContext context, out string reason)
        {
            var missing = context.WIP.TimelineMap.GetFullPaths(context.WIP.ParentPath).FirstOrDefault(path => !File.Exists(path));
            if (missing != null)
            {
                reason = $"The video '{Path.GetFileName(missing)}' is not there.";
                return false;
            }
            reason = null;
            return true;
        }

        protected override bool IsFinished(SubtitleGeneratorContext context)
        {
            return context.WIP.TimelineMap.GetFullPaths(context.WIP.ParentPath).All(path => File.Exists(Path.ChangeExtension(path, this.FileSuffix)));
        }

        protected override void DoWork(SubtitleGeneratorContext context)
        {
            var segments = context.WIP.TimelineMap.Segments.ToArray();
            // (taken for the first part to do, kept until the last one)
            IDisposable encodingLock = null;
            try
            {
                for (var index = 0; index < segments.Length; index++)
                {
                    var segment = segments[index];
                    var sourcePath = Path.Combine(context.WIP.ParentPath, segment.Filename);
                    var outputPath = Path.ChangeExtension(sourcePath, this.FileSuffix);
                    if (File.Exists(outputPath))
                        continue;

                    // (safe point between two parts: a part that is done is kept; Pause / Close stop here, before the next one.
                    //  While paused, the lock is released: the other scenes can encode)
                    if (context.Output.GetPauseAtSafePointReason(context.SceneNodeId) != null)
                    {
                        encodingLock?.Dispose();
                        encodingLock = null;
                        context.PauseIfClosing();
                    }
                    encodingLock ??= context.Locks.VideoEncoding.Acquire(segments.Length > 1
                        ? $"ffmpeg: creating the light videos of the {segments.Length} parts"
                        : "ffmpeg: creating the light video");

                    var partText = segments.Length > 1 ? $" (part {index + 1} of {segments.Length})" : string.Empty;
                    context.RunStep($"Create '{Path.GetFileName(outputPath)}'{partText}", node =>
                    {
                        // (not the real name until it's complete: a stopped ffmpeg leaves no incomplete file that looks done.
                        //  '.partial': not a video extension, so not found as a new video by a rescan)
                        var partialPath = outputPath + ".partial";
                        try
                        {
                            context.FfmpegHelper.CreateVideo(
                                sourcePath,
                                partialPath,
                                GetFfmpegFormat(outputPath),
                                this.FfmpegParameters,
                                segment.Duration,
                                "Stop the creation of the video (it will be created again, from the start, at the next run).",
                                // (only the Stop button: Close / Pause wait for the end of the part, see the safe point below)
                                () => context.PollStopRequest());
                            File.Move(partialPath, outputPath, overwrite: true);
                        }
                        catch (OperationCanceledException)
                        {
                            throw new UserStoppedWorkerException();
                        }
                        finally
                        {
                            if (File.Exists(partialPath))
                                File.Delete(partialPath);
                        }
                    });
                }
            }
            finally
            {
                encodingLock?.Dispose();
            }
        }

        /// <summary>The container of the video (ffmpeg -f), from the extension of its real name (the temporary name doesn't tell it).</summary>
        private static string GetFfmpegFormat(string outputPath)
        {
            var extension = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
            switch (extension)
            {
                case "mp4":
                case "mov":
                case "webm":
                case "avi":
                    return extension;
                case "mkv":
                    return "matroska";
                default:
                    throw new Exception($"The extension of '{Path.GetFileName(outputPath)}' (FileSuffix) is not a video format known by this output: use .mp4, .mov, .webm, .avi or .mkv.");
            }
        }
    }
}
