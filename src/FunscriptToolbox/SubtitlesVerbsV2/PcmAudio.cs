using System;

namespace FunscriptToolbox.SubtitlesVerbV2
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
            this.SamplingRate = samplingRate;
            this.Data = data;
            this.Offset = offset ?? TimeSpan.Zero;
        }

        public PcmAudio ExtractSnippet(TimeSpan startTime, TimeSpan endTime)
        {
            var startIndex = TimeSpanToIndex(startTime);
            var endIndex = Math.Min(TimeSpanToIndex(endTime), Data.Length);
            var durationLength = endIndex - startIndex;

            byte[] snippetData = new byte[durationLength];
            Array.Copy(this.Data, startIndex, snippetData, 0, durationLength);

            return new PcmAudio(this.SamplingRate, snippetData, startTime);
        }

        private TimeSpan IndexToTimeSpan(int index) => TimeSpan.FromSeconds((double)index / SamplingRate / NbBytesPerSamples);
        private int TimeSpanToIndex(TimeSpan index) => (int)(index.TotalSeconds * SamplingRate) * NbBytesPerSamples;
    }
}