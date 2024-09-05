using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xabe.FFmpeg;

namespace AudioSynchronization
{
    public static class AudioTracksAnalyzer
    {
        public static AudioSignature ExtractSignature(string filename, int nbSamplesPerSeconds = 120)
        {
            return AudioSignature.FromSamples(
                nbSamplesPerSeconds,
                ExtractSamples(filename, nbSamplesPerSeconds).ToArray());
        }


        private static IEnumerable<ushort> ExtractSamples(string filename, int nbSamplesPerSeconds)
        {
            var tempFile = Path.GetTempFileName() + ".raw";
            File.Delete(tempFile);

            //create a command adding a video file
            var nbSamplesPerPeak = 200;

            var mediaInfo = FFmpeg.GetMediaInfo(filename).GetAwaiter().GetResult();
            var audioStream = mediaInfo.AudioStreams.FirstOrDefault();
            FFmpeg.Conversions.New()
                .AddStream(audioStream)
                .SetOverwriteOutput(true)
                .AddParameter($"-f s16le -acodec pcm_s16le -vn -ac 1 -ar {nbSamplesPerSeconds * nbSamplesPerPeak}")
                .SetOutput(tempFile)
                .Start()
                .Wait();

            try
            {
                using (var file = File.Open(tempFile, FileMode.Open, FileAccess.Read))
                {
                    var nbSampleInCurrentPeak = nbSamplesPerPeak / 2;
                    var maxValue = ushort.MinValue;

                    while (true)
                    {
                        var b1 = file.ReadByte();
                        var b2 = file.ReadByte();
                        if (b1 < 0 || b2 < 0)
                        {
                            yield return maxValue;
                            yield break;
                        }
                        var value = (short)((b2 << 8) + b1);

                        nbSampleInCurrentPeak--;
                        if (nbSampleInCurrentPeak <= 0)
                        {
                            yield return maxValue;
                            nbSampleInCurrentPeak = nbSamplesPerPeak;
                            maxValue = ushort.MinValue;
                        }
                        else
                        {
                            maxValue = Math.Max(maxValue, (ushort)Math.Abs((int)value));
                        }
                    }
                }
            }
            finally
            {
                File.Delete(tempFile);
            }
        }
    }
}
