using System;
using System.Reflection;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcription
{
    internal class PcmAudio
    {
        private const int NbBytesPerSamples = 2;

        public int SamplingRate { get; }
        public byte[] Data { get; }
        public TimeSpan Offset { get; }
        public TimeSpan Duration => IndexToTimeSpan(Data.Length);

        public PcmAudio(int samplingRate, byte[] data, TimeSpan? offset = null)
        {
            SamplingRate = samplingRate;
            Data = data;
            Offset = offset ?? TimeSpan.Zero;
        }

        public PcmAudio ExtractSnippet(TimeSpan startTime, TimeSpan endTime)
        {
            var startIndex = TimeSpanToIndex(startTime);
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