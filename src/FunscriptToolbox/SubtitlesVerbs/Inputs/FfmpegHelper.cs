using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Progress;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Xabe.FFmpeg;

namespace FunscriptToolbox.SubtitlesVerbs.Inputs
{
    public class FfmpegHelper
    {
        protected const int SamplingRate = 16000;

        private readonly SharedLocks r_locks;

        /// <param name="locks">
        /// When several videos are processed in parallel, the ffmpeg processes are limited, by kind of work (see <see cref="SharedLocks"/>):
        /// reading the original video, working on a whole audio. (The audio clips sent to the AIs are made in memory: no ffmpeg.)
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

        /// <param name="showProgress">Shows the progress of ffmpeg (%) on the current task (false when the task already counts something else, ex. files).</param>
        public void ConvertPcmAudioToOtherFormat(
            PcmAudio pcmAudio,
            string outputFilepath,
            string ffmpegParameters = null,
            bool showProgress = true)
        {
            using var localLock = r_locks.Audio.Acquire("ffmpeg: converting audio");
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
                if (showProgress)
                {
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
            // (ffmpeg is started here, not by Xabe.FFmpeg: stopped, Xabe asks ffmpeg to quit and returns without waiting for it, ffmpeg went on
            //  in the background, still writing the output (it couldn't be deleted). Here, it's killed (with its children) and waited for)
            var node = TaskScope.Current;
            var formatParameter = outputFormat == null ? string.Empty : $" -f {outputFormat}";
            var psi = new ProcessStartInfo(this.FfmpegPath)
            {
                // (-progress pipe:1: the position reached, on the standard output; -nostdin: never waits for a key)
                Arguments = $"-hide_banner -nostdin -y -i \"{inputPath}\" {ffmpegParameters}{formatParameter} -progress pipe:1 -nostats \"{outputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // (the end of the errors of ffmpeg: for the message when it fails)
            var errorLines = new Queue<string>();
            node.SetCancellable(stopHint);
            using var process = new Process { StartInfo = psi };
            try
            {
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (errorLines)
                    {
                        errorLines.Enqueue(e.Data);
                        if (errorLines.Count > 20) errorLines.Dequeue();
                    }
                };
                process.OutputDataReceived += (_, e) =>
                {
                    // out_time_us=<microseconds> (also out_time_ms, in microseconds too)
                    if (e.Data == null || inputDuration <= TimeSpan.Zero || !e.Data.StartsWith("out_time_us=")) return;
                    if (long.TryParse(e.Data.Substring("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                        node.SetProgress(Math.Max(0, Math.Min(100, (int)(100 * us / 1_000_000.0 / inputDuration.TotalSeconds))), 100, "%");
                };
                process.Start();
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                while (!process.WaitForExit(250))
                {
                    if (shouldStop())
                    {
                        KillAndWait(process);
                        throw new OperationCanceledException("The creation of the video was stopped.");
                    }
                }
                // (the end of the redirected output / errors)
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    string errors;
                    lock (errorLines) { errors = string.Join(Environment.NewLine, errorLines); }
                    throw new Exception($"ffmpeg failed (exit code {process.ExitCode}) creating '{Path.GetFileName(outputPath)}':{Environment.NewLine}{errors}");
                }
            }
            finally
            {
                // (an exception of ours, ex. the application closing: ffmpeg doesn't stay behind)
                KillAndWait(process);
                node.SetCancellable(null);
            }
        }

        /// <summary>Kills a process that is still running, with its children, and waits for it (its files are released after).</summary>
        private static void KillAndWait(Process process)
        {
            try
            {
                if (process.HasExited)
                    return;
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10000);
            }
            catch (InvalidOperationException)
            {
                // (not started, or already gone)
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // (ending by itself at the same time)
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