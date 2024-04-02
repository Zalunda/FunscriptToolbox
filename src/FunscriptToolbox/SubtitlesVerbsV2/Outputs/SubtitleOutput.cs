using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbV2;
using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbsV2.Outputs
{
    internal abstract class SubtitleOutput
    {
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;
        
        [JsonIgnore()]
        public abstract bool NeedSubtitleForcedTimings { get; }

        public abstract void CreateOutput(
            SubtitleGeneratorContext context,
            WorkInProgressSubtitles wipsub);
    }
}
