using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Progress;
using System;
using System.IO;
using System.Linq;
using Xabe.FFmpeg;

namespace FunscriptToolbox.SubtitlesVerbs.AudioExtractions
{
    /// <summary>What an ffmpeg audio conversion is for: it decides how many can run at the same time (see <see cref="SharedLocks"/>).</summary>
    public enum FfmpegWorkload
    {
        /// <summary>A whole audio (saving an extraction, the input of a local tool, ...).</summary>
        Audio,

        /// <summary>A piece of audio cut from the PCM in memory and written in the temp folder (many, small).</summary>
        Clip,
    }

    public class FfmpegHelper
    {
        protected const int SamplingRate = 16000;

        private readonly SharedLocks r_locks;

        /// <param name="locks">
        /// When several videos are processed in parallel, the ffmpeg processes are limited, by kind of work (see <see cref="SharedLocks"/>):
        /// reading the original video, working on a whole audio, cutting audio clips.
        /// </param>
        public FfmpegHelper(SharedLocks locks)
        {
            r_locks = locks;
        }

        /// <summary>The ffmpeg executable, from the folder given to Xabe.FFmpeg (a clear error when it is not configured or not there).</summary>
        public string FfmpegPath => GetExecutablePath("ffmpeg");

        /// <summary>The ffprobe executable (same rules as <see cref="FfmpegPath"/>).</summary>
        public string FfprobePath => GetExecutablePath("ffprobe");

        private static string GetExecutablePath(string name)
        {
            if (string.IsNullOrEmpty(FFmpeg.ExecutablesPath))
                throw new InvalidOperationException($"The folder of ffmpeg/ffprobe is not configured, so '{name}' can't be found.");

            var path = Path.Combine(FFmpeg.ExecutablesPath, OperatingSystem.IsWindows() ? name + ".exe" : name);
            if (!File.Exists(path))
                throw new FileNotFoundException($"'{name}' was not found in the ffmpeg folder '{FFmpeg.ExecutablesPath}'.", path);
            return path;
        }

        public string EscapeFfmpegDrawtext(string text)
        {
            // The order is important. Backslash must be escaped first.
            return text.Replace("\\", "\\\\")
                       .Replace("'", "\\'")
                       .Replace(":", "\\:")
                       .Replace("%", "\\%");
        }

        /// <remarks>The caller holds the lock 'VID' (SharedLocks.VideoSource): ex. for all the parts of a scene, one after the other.</remarks>
        public PcmAudio ExtractPcmAudio(
            string inputPath,
            string extractionParameters = null, 
            int samplingRate = SamplingRate)
        {
            if (!File.Exists(inputPath))
            {
                throw new ArgumentException($"File '{inputPath}' doesn't exists.");
            }

            var tempPcmFile = PathExtension.GetTempFileName(".pcm");
            try
            {
                IMediaInfo mediaInfo = FFmpeg.GetMediaInfo(inputPath).GetAwaiter().GetResult();
                IStream audioStream = mediaInfo.AudioStreams.FirstOrDefault();
                var conversion = FFmpeg.Conversions.New()
                    .AddStream(audioStream)
                    .SetOverwriteOutput(true)
                    .AddParameter($"-f s16le -acodec pcm_s16le -vn -ac 1 -ar {SamplingRate} {extractionParameters}")
                    .SetOutput(tempPcmFile);
                // The input is the real video (its duration is known): the progress of ffmpeg is shown on the current task.
                // (the node is taken now: the event is raised on another thread)
                var node = TaskScope.Current;
                conversion.OnProgress += (_, args) => node.SetProgress(args.Percent, 100, "%");
                conversion
                    .Start()
                    .Wait();
                return new PcmAudio(samplingRate, File.ReadAllBytes(tempPcmFile));
            }
            finally
            {
                File.Delete(tempPcmFile);
            }
        }

        /// <param name="workload">Clip: a piece of audio cut from the PCM in memory (many, small). Audio: a whole audio.</param>
        /// <param name="showProgress">Shows the progress of ffmpeg (%) on the current task (false when the task already counts something else, ex. files).</param>
        public void ConvertPcmAudioToOtherFormat(
            PcmAudio pcmAudio,
            string outputFilepath,
            string ffmpegParameters = null,
            FfmpegWorkload workload = FfmpegWorkload.Audio,
            bool showProgress = true)
        {
            using var localLock = (workload == FfmpegWorkload.Clip ? r_locks.Clips : r_locks.Audio)
                .Acquire(workload == FfmpegWorkload.Clip ? "ffmpeg: writing an audio clip" : "ffmpeg: converting audio");
            var tempPcmFile = PathExtension.GetTempFileName(".pcm");
            try
            {
                using (var writer = File.Create(tempPcmFile))
                {
                    writer.Write(pcmAudio.Data, 0, pcmAudio.Data.Length);
                }
                var conversion = FFmpeg.Conversions.New()
                    .SetOverwriteOutput(true)
                    .AddParameter($"-f s16le -ar {pcmAudio.SamplingRate} -ac 1 -i \"{tempPcmFile}\" -acodec pcm_s16le -vn -ar 16000 {ffmpegParameters}")
                    .SetOutput(outputFilepath);
                if (workload == FfmpegWorkload.Audio && showProgress)
                {
                    // (a clip is too short to show a progress)
                    ShowProgressOnCurrentTask(conversion, pcmAudio.Duration);
                }
                conversion
                    .Start()
                    .Wait();
            }
            finally
            {
                File.Delete(tempPcmFile);
            }
        }

        internal PcmAudio TransformPcmAudio(
            PcmAudio pcmAudio, 
            string ffmpegParameters)
        {
            using var localLock = r_locks.Audio.Acquire("ffmpeg: transforming audio");
            var tempPcmSourceFile = PathExtension.GetTempFileName(".pcm");
            var tempPcmDestinationFile = PathExtension.GetTempFileName(".pcm");
            try
            {
                using (var writer = File.Create(tempPcmSourceFile))
                {
                    writer.Write(pcmAudio.Data, 0, pcmAudio.Data.Length);
                }
                var conversion = FFmpeg.Conversions.New()
                    .SetOverwriteOutput(true)
                    .AddParameter($"-f s16le -ar {pcmAudio.SamplingRate} -ac 1 -i \"{tempPcmSourceFile}\" {ffmpegParameters} -f s16le -acodec pcm_s16le -vn -ar {pcmAudio.SamplingRate} -ac 1")
                    .SetOutput(tempPcmDestinationFile);
                ShowProgressOnCurrentTask(conversion, pcmAudio.Duration);
                conversion
                    .Start()
                    .Wait();
                return new PcmAudio(pcmAudio.SamplingRate, File.ReadAllBytes(tempPcmDestinationFile));

            }
            finally
            {
                File.Delete(tempPcmSourceFile);
                File.Delete(tempPcmDestinationFile);
            }
        }

        /// <summary>
        /// The input is raw PCM: ffmpeg doesn't know its duration (no percent), but we do. The progress is computed from
        /// the position reached by ffmpeg, and shown on the current task (taken now: the event is raised on another thread).
        /// </summary>
        private static void ShowProgressOnCurrentTask(IConversion conversion, TimeSpan inputDuration)
        {
            if (inputDuration <= TimeSpan.Zero)
                return;

            var node = TaskScope.Current;
            conversion.OnProgress += (_, args) =>
            {
                var percent = Math.Max(0, Math.Min(100, (int)(100 * args.Duration.TotalSeconds / inputDuration.TotalSeconds)));
                node.SetProgress(percent, 100, "%");
            };
        }

        /// <summary>
        /// Creates a new video from a video (ex. a light version of it), with the progress (%) on the current task. Long: one at a time (lock 'ENC'),
        /// and it can be stopped (the current task shows a 'Stop' button): 'shouldStop' is asked regularly, on the calling thread.
        /// </summary>
        /// <param name="ffmpegParameters">What is between the input (-i) and the output, ex. the filters and the codecs.</param>
        /// <param name="outputFormat">The container (ffmpeg -f), when the name of the output doesn't tell it (ex. a temporary name).</param>
        /// <exception cref="OperationCanceledException">Stopped: what was written is incomplete.</exception>
        /// <remarks>The caller holds the lock 'ENC' (SharedLocks.VideoEncoding): ex. for all the parts of a scene, one after the other.</remarks>
        internal void CreateVideo(string inputPath, string outputPath, string outputFormat, string ffmpegParameters, TimeSpan inputDuration, string stopHint, Func<bool> shouldStop)
        {
            var node = TaskScope.Current;
            using var cts = new System.Threading.CancellationTokenSource();
            var formatParameter = outputFormat == null ? string.Empty : $" -f {outputFormat}";
            var conversion = FFmpeg.Conversions.New()
                .SetOverwriteOutput(true)
                .AddParameter($"-i \"{inputPath}\" {ffmpegParameters}{formatParameter}")
                .SetOutput(outputPath);
            ShowProgressOnCurrentTask(conversion, inputDuration);
            node.SetCancellable(stopHint);
            try
            {
                var task = conversion.Start(cts.Token);
                while (!task.Wait(TimeSpan.FromMilliseconds(250)))
                {
                    if (!cts.IsCancellationRequested && shouldStop())
                        cts.Cancel(); // (ffmpeg is killed: the task ends with an OperationCanceledException)
                }
                task.GetAwaiter().GetResult();
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException canceled)
            {
                throw canceled;
            }
            finally
            {
                node.SetCancellable(null);
            }
        }

        internal byte[] TakeScreenshotAsBytes(string videoPath, TimeSpan time, string extension = ".jpg", string filter = null)
        {
            using var localLock = r_locks.VideoSource.Acquire("ffmpeg: taking screenshot");
            var tempFile = PathExtension.GetTempFileName(extension);
            try
            {
                var filterAddon = filter == null ? string.Empty : $"-vf \"{filter}\"";
                FFmpeg.Conversions.New()
                    .SetOverwriteOutput(true)
                    .AddParameter($"-ss {time} -i \"{videoPath}\" -frames:v 1 -update 1 {filterAddon}")
                    .SetOutput(tempFile)
                    .Start()
                    .Wait();
                return File.Exists(tempFile) ? File.ReadAllBytes(tempFile) : null;

            }
            finally
            {
                File.Delete(tempFile);
            }
        }
    }
}