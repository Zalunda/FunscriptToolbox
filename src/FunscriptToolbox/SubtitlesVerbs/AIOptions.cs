using Newtonsoft.Json;

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
    }
}