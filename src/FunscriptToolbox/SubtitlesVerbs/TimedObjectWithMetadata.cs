using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public class TimedObjectWithMetadata
    {
        public TimeSpan StartTime { get; }
        public TimeSpan EndTime { get; }
        public TimeSpan Duration => EndTime - StartTime;
        public MetadataCollection Metadata { get; }
        
        public TimedObjectWithMetadata(
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

        public void Merge(TimedObjectWithMetadata other)
        {
            this.Metadata.Merge(other.Metadata);
        }
    }

    public class TimedObjectWithMetadata<T> : TimedObjectWithMetadata
    {
        public T Tag { get; set; }

        public TimedObjectWithMetadata(
            TimeSpan startTime,
            TimeSpan endTime,
            IEnumerable<KeyValuePair<string, string>> metadata = null)
            : base(startTime, endTime, metadata)
        {
        }
    }
}