using FunscriptToolbox.Core.Infra;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class TimedItemWithMetadata : ITiming
    {
        private TimeSpan r_startTime;
        private TimeSpan r_endTime;
        // Always rounded to the millisecond: StartTime is used as a key in the WorkInProgress files.
        public TimeSpan StartTime { get => r_startTime; set => r_startTime = value.RoundToMs(); }
        public TimeSpan EndTime { get => r_endTime; set => r_endTime = value.RoundToMs(); }
        public TimeSpan Duration => EndTime - StartTime;
        public MetadataCollection Metadata { get; }
        public DateTime? CreationTime { get; }
        public TimedItemWithMetadata(
            TimeSpan startTime,
            TimeSpan endTime,
            IEnumerable<KeyValuePair<string, string>> metadata = null,
            DateTime? creationTime = null)
        {
            StartTime = startTime;
            EndTime = endTime;

            this.Metadata = new MetadataCollection();
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    this.Metadata[kvp.Key] = kvp.Value;
                }
            }
            CreationTime = creationTime ?? DateTime.Now;
        }
    }
}