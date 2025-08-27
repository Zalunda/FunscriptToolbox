using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class TimedItemWithMetadata : ITiming
    {
        public TimeSpan StartTime { get; }
        public TimeSpan EndTime { get; }
        public TimeSpan Duration => EndTime - StartTime;
        public MetadataCollection Metadata { get; }
        
        public TimedItemWithMetadata(
            TimeSpan startTime, 
            TimeSpan endTime, 
            IEnumerable<KeyValuePair<string, string>> metadata = null)
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
        }

        public void Merge(TimedItemWithMetadata other)
        {
            this.Metadata.Merge(other.Metadata);
        }
    }
}