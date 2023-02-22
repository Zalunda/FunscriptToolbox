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

        public FunscriptAction()
        {

        }

        public FunscriptAction(int at, int pos)
        {
            this.At = at;
            this.Pos = pos;
        }

        public override string ToString()
        {
            return $"{At}, {Pos}";
        }
    }
}