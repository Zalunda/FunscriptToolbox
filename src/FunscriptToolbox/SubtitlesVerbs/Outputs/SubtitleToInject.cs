using FunscriptToolbox.SubtitlesVerbs;
using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.Core
{
    public class SubtitleToInject
    {
        [ConfigDoc("Where the subtitle is placed: \"Start\" (from the start of the video) or \"End\" (from its end). It never covers the first (Start) / the last (End) real subtitle: it's shortened instead.")]
        [JsonProperty(Order = 1, Required = Required.Always)]
        public SubtitleToInjectOrigin Origin { get; set; }
        [ConfigDoc("A scene made of several videos (.vseq): true = in the file of each video; false = only once, in the scene.")]
        [JsonProperty(Order = 2)]
        public bool InjectInAllFiles {get; set;} = true;
        [ConfigDoc("The distance from the Origin.")]
        [JsonProperty(Order = 3, Required = Required.Always)]
        public TimeSpan OffsetTime { get; set; }
        [ConfigDoc("How long the subtitle is shown.")]
        [JsonProperty(Order = 4, Required = Required.Always)]
        public TimeSpan Duration { get; set; }
        [ConfigDoc("The lines of the subtitle.")]
        [JsonProperty(Order = 5, Required = Required.Always)]
        public string[] Lines { get; set; }
    }
}
