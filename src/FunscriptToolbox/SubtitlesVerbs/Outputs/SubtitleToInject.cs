using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.Core
{
    public class SubtitleToInject
    {
        [JsonProperty(Order = 1, Required = Required.Always)]
        public SubtitleToInjectOrigin Origin { get; set; }
        [JsonProperty(Order = 2)]
        public bool InjectInAllFiles {get; set;} = true;
        [JsonProperty(Order = 3, Required = Required.Always)]
        public TimeSpan OffsetTime { get; set; }
        [JsonProperty(Order = 4, Required = Required.Always)]
        public TimeSpan Duration { get; set; }
        [JsonProperty(Order = 5, Required = Required.Always)]
        public string[] Lines { get; set; }
    }
}
