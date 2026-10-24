using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.AudioExtractions
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
        [JsonIgnore]
        public TimeSpan Offset { get; }
        public TimeSpan Duration { get; }
        [JsonIgnore]
        public TimeSpan StartTime => this.Offset;
        [JsonIgnore]
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

        public float[] ToFloat()
        {
            byte[] bytes = this.Data;
            int numSamples = bytes.Length / 2;
            float[] samples = new float[numSamples];

            for (int i = 0; i < numSamples; i++)
            {
                short sample = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
                samples[i] = sample / 32768.0f;
            }

            return samples;
        }


        public float[] ToRms(int frameSizeInMs)
        {
            var samples = this.ToFloat();
            int frameSize = (SamplingRate * frameSizeInMs) / 1000;
            int n = samples.Length / frameSize;
            float[] rms = new float[n];
            for (int i = 0; i < n; i++)
            {
                float sumSq = 0;
                int offset = i * frameSize;
                for (int j = 0; j < frameSize; j++)
                {
                    sumSq += samples[offset + j] * samples[offset + j];
                }
                rms[i] = (float)Math.Sqrt(sumSq / frameSize);
            }
            return rms;
        }

        private static TimeSpan IndexToTimeSpan(int index, int samplingRate) => TimeSpan.FromSeconds((double)index / samplingRate / NbBytesPerSamples);
        private int TimeSpanToIndex(TimeSpan index) => (int)(index.TotalSeconds * SamplingRate) * NbBytesPerSamples;
    }
}