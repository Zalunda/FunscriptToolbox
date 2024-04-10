using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbsV2.AudioExtraction
{
    public class PcmAudio
    {
        private const int NbBytesPerSamples = 2;

        public int SamplingRate { get; }
        public byte[] Data { get; }
        public TimeSpan Offset { get; }
        public TimeSpan Duration => IndexToTimeSpan(Data.Length);

        public AudioNormalizationRule[] AudioNormalizationRules { get; }

        [JsonConstructor]
        public PcmAudio(int samplingRate, byte[] data, TimeSpan? offset = null, IEnumerable<AudioNormalizationRule> audioNormalizationRules = null)
        {
            SamplingRate = samplingRate;
            Data = data;
            Offset = offset ?? TimeSpan.Zero;
            AudioNormalizationRules = audioNormalizationRules == null ? null : audioNormalizationRules.ToArray();
        }

        public PcmAudio(IEnumerable<PcmAudio> parts, IEnumerable<AudioNormalizationRule> audioNormalizationRules)
        {
            SamplingRate = parts.First().SamplingRate;
            var dataBuilder = new MemoryStream();
            foreach (var part in parts)
            { 
                dataBuilder.Write(part.Data, 0, part.Data.Length);
            }
            Data = dataBuilder.ToArray();
            Offset = TimeSpan.Zero;
            AudioNormalizationRules = audioNormalizationRules.ToArray(); ;
        }

        public PcmAudio ExtractSnippet(TimeSpan startTime, TimeSpan endTime)
        {
            var startIndex = Math.Max(0, TimeSpanToIndex(startTime));
            var endIndex = Math.Min(TimeSpanToIndex(endTime), Data.Length);
            var durationLength = endIndex - startIndex;

            byte[] snippetData = new byte[durationLength];
            Array.Copy(Data, startIndex, snippetData, 0, durationLength);

            return new PcmAudio(SamplingRate, snippetData, startTime);
        }

        public PcmAudio GetSilenceAudio(TimeSpan duration)
        {
            var data = new byte[TimeSpanToIndex(duration)];
            return new PcmAudio(SamplingRate, data);
        }

        private TimeSpan IndexToTimeSpan(int index) => TimeSpan.FromSeconds((double)index / SamplingRate / NbBytesPerSamples);
        private int TimeSpanToIndex(TimeSpan index) => (int)(index.TotalSeconds * SamplingRate) * NbBytesPerSamples;
    }
}