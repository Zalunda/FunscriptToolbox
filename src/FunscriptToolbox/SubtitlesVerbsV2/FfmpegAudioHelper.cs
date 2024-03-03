using System;
using System.IO;
using System.Linq;
using Xabe.FFmpeg;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    internal class FfmpegAudioHelper : IFfmpegAudioHelper
    {
        protected const int SamplingRate = 16000;

        public FfmpegAudioHelper()
        {

        }

        public PcmAudio ExtractPcmAudio(string inputPath, int samplingRate = SamplingRate, string extractionParameters = null)
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
                    .AddParameter($"-f s16le -acodec pcm_s16le -ac 1 -ar {SamplingRate} {extractionParameters}")
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

        public void ConvertPcmAudioToWavFile(PcmAudio pcmAudio, string outputWavFilepath)
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
                    .AddParameter($"-f s16le -ar 16000 -ac 1 -i \"{tempPcmFile}\" -acodec pcm_s16le")
                    .SetOutput(outputWavFilepath)
                    .Start()
                    .Wait();
            }
            finally
            {
                File.Delete(tempPcmFile);
            }
        }
    }
}