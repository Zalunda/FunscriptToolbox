using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public class AIOptions
    {
        [JsonProperty(Order = 1)]
        public AIPrompt SystemPrompt { get; set; }

        [JsonProperty(Order = 2)]
        public AIPrompt FirstUserPrompt { get; set; }

        [JsonProperty(Order = 3)]
        public AIPrompt OtherUserPrompt { get; set; }

        [JsonProperty(Order = 4)]
        public bool IncludeStartTime { get; set; } = true;

        [JsonProperty(Order = 5)]
        public bool IncludeEndTime { get; set; } = false;

        [JsonProperty(Order = 6)]
        public bool IncludeContext { get; set; } = true;

        [JsonProperty(Order = 7)]
        public bool IncludeTalker { get; set; } = true;

        [JsonProperty(Order = 8)]
        public bool IncludeParts { get; set; } = false;

        // Static helper method to augment metadata with SubtitlesForcedTiming data
        public Dictionary<string, object> CreateMetadata(
            SubtitleForcedTimingCollection forcedTiming,
            TimeSpan startTime,
            TimeSpan endTime,
            ref string ongoingContext)
        {
            var metadata = new Dictionary<string, object>();

            if (this.IncludeStartTime)
            {
                metadata["StartTime"] = startTime.ToString(@"hh\:mm\:ss\.fff");
            }

            if (this.IncludeEndTime)
            {
                metadata["EndTime"] = endTime.ToString(@"hh\:mm\:ss\.fff");
            }

            if (forcedTiming != null)
            {
                if (this.IncludeContext)
                {
                    var context = forcedTiming.GetContextAt(startTime);
                    if (context != null && context != ongoingContext)
                    {
                        if (!string.IsNullOrEmpty(context))
                        {
                            metadata["Context"] = context;
                            ongoingContext = context;
                        }
                    }
                }

                if (this.IncludeTalker)
                {
                    var talker = forcedTiming.GetTalkerAt(startTime, endTime);
                    if (!string.IsNullOrEmpty(talker))
                    {
                        metadata["Talker"] = talker;
                    }
                }
            }

            return metadata;
        }
    }
}