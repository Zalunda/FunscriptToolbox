using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.Core
{
    public class FunscriptAction
    {
        [JsonProperty("at")]
        public int At { get; set; }
        [JsonProperty("pos")]
        public int Pos { get; set; }

        [JsonIgnore]
        public TimeSpan AtAsTimeSpan => TimeSpan.FromMilliseconds(this.At);
    }
}