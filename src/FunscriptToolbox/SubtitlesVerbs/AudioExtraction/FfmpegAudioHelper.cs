using System;
using System.IO;
using System.Linq;
using Xabe.FFmpeg;

namespace FunscriptToolbox.SubtitlesVerbs.AudioExtraction
{
    public class FfmpegAudioHelper
    {
        protected const int SamplingRate = 16000;

        public FfmpegAudioHelper()
        {

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

            var tempPcmFile = Path.GetTempFileName() + ".pcm";
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

        public void ConvertPcmAudioToWavFile(
            PcmAudio pcmAudio,
            string outputWavFilepath,
            string ffmpegParameters = null)
        {
            var tempPcmFile = Path.GetTempFileName() + ".pcm";
            try
            {
                using (var writer = File.Create(tempPcmFile))
                {
                    writer.Write(pcmAudio.Data, 0, pcmAudio.Data.Length);
                }
                FFmpeg.Conversions.New()
                    .SetOverwriteOutput(true)
                    .AddParameter($"-f s16le -ar 16000 -ac 1 -i \"{tempPcmFile}\" -acodec pcm_s16le -vn -ar 16000 {ffmpegParameters}")
                    .SetOutput(outputWavFilepath)
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
            var tempPcmSourceFile = Path.GetTempFileName() + ".pcm";
            var tempPcmDestinationFile = Path.GetTempFileName() + ".pcm";
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
            var tempFile = Path.GetTempFileName() + extension;
            try
            {
                var filterAddon = filter == null ? string.Empty : $"-vf \"{filter}\"";
                FFmpeg.Conversions.New()
                    .SetOverwriteOutput(true)
                    .AddParameter($"-i \"{videoPath}\" -ss {time} -frames:v 1 {filterAddon}")
                    .SetOutput(tempFile)
                    .Start()
                    .Wait();
                return File.ReadAllBytes(tempFile);

            }
            finally
            {
                File.Delete(tempFile);
            }
        }
    }
}