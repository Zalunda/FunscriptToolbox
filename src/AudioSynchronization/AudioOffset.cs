using Newtonsoft.Json;
using System;

namespace AudioSynchronization
{
    public class AudioOffset
    {
        public AudioOffset(TimeSpan startTime, TimeSpan endTime, TimeSpan? offset)
        {
            StartTime = startTime;
            EndTime = endTime;
            Offset = offset;
        }

        public TimeSpan StartTime { get; }
        public TimeSpan EndTime { get; }
        [JsonIgnore]
        public TimeSpan Duration => this.EndTime - this.StartTime;
        public TimeSpan? Offset { get; }

        [JsonIgnore]
        public int NbTimesUsed { get; private set; }

        public void IncrementUsage()
        {
            NbTimesUsed++;
        }
        internal void ResetUsage()
        {
            NbTimesUsed = 0;
        }

        public override string ToString()
        {
            return $"{this.StartTime};{this.EndTime};{this.Offset}";
        }
    }
}