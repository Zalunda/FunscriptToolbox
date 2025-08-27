using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class TimedItemWithMetadataTagged : TimedItemWithMetadata
    {
        public dynamic[] BinaryContents { get; set; }

        public TimedItemWithMetadataTagged(
            TimedItemWithMetadata original,
            IEnumerable<KeyValuePair<string, string>> otherMetadata = null)
            : this(original.StartTime, original.EndTime, original.Metadata, otherMetadata)
        {
        }

        public TimedItemWithMetadataTagged(
            TimeSpan startTime,
            TimeSpan endTime,
            IEnumerable<KeyValuePair<string, string>> metadata = null,
            IEnumerable<KeyValuePair<string, string>> otherMetadata = null)
            : base(startTime, endTime, metadata)
        {
            if (otherMetadata != null)
            {
                // Add or override metadata from first source
                foreach (var kvp in otherMetadata)
                {
                    this.Metadata[kvp.Key] = kvp.Value;
                }
            }
        }
    }
}