using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.AudioExtraction
{
    public class PcmAudio
    {
        private const int NbBytesPerSamples = 2;

        public int SamplingRate { get; }

        private Func<byte[]> r_loadPcmFunc;
        private byte[] r_data = null;

        [JsonIgnore]
        public byte[] Data 
        { 
            get
            {
                r_data ??= r_loadPcmFunc();
                return r_data;
            }
        }
        public TimeSpan Offset { get; }
        public TimeSpan Duration { get; }
        public TimeSpan StartTime => this.Offset;
        public TimeSpan EndTime => this.Offset + this.Duration;

        [JsonConstructor]
        public PcmAudio(int samplingRate, TimeSpan duration, TimeSpan? offset = null)
        {
            SamplingRate = samplingRate;
            r_data = null;
            Duration = duration;
            Offset = offset ?? TimeSpan.Zero;
        }


        public PcmAudio(int samplingRate, byte[] data, TimeSpan? offset = null)
            : this(samplingRate, IndexToTimeSpan(data.Length, samplingRate), offset)
        {
            r_data = data;
        }

        internal void RegisterLoadPcmFunc(Func<byte[]> loadPcmFunc)
        {
            r_loadPcmFunc = loadPcmFunc;
        }

        public PcmAudio(IEnumerable<PcmAudio> parts)
        {
            SamplingRate = parts.First().SamplingRate;
            var dataBuilder = new MemoryStream();
            foreach (var part in parts)
            { 
                dataBuilder.Write(part.Data, 0, part.Data.Length);
            }
            r_data = dataBuilder.ToArray();
            Duration = IndexToTimeSpan(r_data.Length, SamplingRate);
            Offset = TimeSpan.Zero;
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

        private static TimeSpan IndexToTimeSpan(int index, int samplingRate) => TimeSpan.FromSeconds((double)index / samplingRate / NbBytesPerSamples);
        private int TimeSpanToIndex(TimeSpan index) => (int)(index.TotalSeconds * SamplingRate) * NbBytesPerSamples;
    }
}