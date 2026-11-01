using FunscriptToolbox.Core.Infra;
using System;
using System.IO;
using System.Linq;
using Xabe.FFmpeg;

namespace FunscriptToolbox.SubtitlesVerbs.AudioExtractions
{
    public class FfmpegHelper
    {
        protected const int SamplingRate = 16000;

        public FfmpegHelper()
        {

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
                FFmpeg.Conversions.New()
                    .AddStream(audioStream)
                    .SetOverwriteOutput(true)
                    .AddParameter($"-f s16le -acodec pcm_s16le -vn -ac 1 -ar {SamplingRate} {extractionParameters}")
                    .SetOutput(tempPcmFile)
                    .Start()
                    .Wait();
                return new PcmAudio(samplingRate, File.ReadAllBytes(tempPcmFile));
            }
            finally
            {
                File.Delete(tempPcmFile);
            }
        }

        public void ConvertPcmAudioToOtherFormat(
            PcmAudio pcmAudio,
            string outputFilepath,
            string ffmpegParameters = null)
        {
            var tempPcmFile = PathExtension.GetTempFileName(".pcm");
            try
            {
                using (var writer = File.Create(tempPcmFile))
                {
                    writer.Write(pcmAudio.Data, 0, pcmAudio.Data.Length);
                }
                FFmpeg.Conversions.New()
                    .SetOverwriteOutput(true)
                    .AddParameter($"-f s16le -ar {pcmAudio.SamplingRate} -ac 1 -i \"{tempPcmFile}\" -acodec pcm_s16le -vn -ar 16000 {ffmpegParameters}")
                    .SetOutput(outputFilepath)
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
            var tempPcmSourceFile = PathExtension.GetTempFileName(".pcm");
            var tempPcmDestinationFile = PathExtension.GetTempFileName(".pcm");
            try
            {
                using (var writer = File.Create(tempPcmSourceFile))
                {
                    writer.Write(pcmAudio.Data, 0, pcmAudio.Data.Length);
                }
                FFmpeg.Conversions.New()
                    .SetOverwriteOutput(true)
                    .AddParameter($"-f s16le -ar {pcmAudio.SamplingRate} -ac 1 -i \"{tempPcmSourceFile}\" {ffmpegParameters} -f s16le -acodec pcm_s16le -vn -ar {pcmAudio.SamplingRate} -ac 1")
                    .SetOutput(tempPcmDestinationFile)
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

        internal byte[] TakeScreenshotAsBytes(string videoPath, TimeSpan time, string extension = ".jpg", string filter = null)
        {
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