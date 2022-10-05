using Newtonsoft.Json;

namespace FunscriptToolbox
{
    public class FunscriptAction
    {
        [JsonProperty("at")]
        public int At { get; set; }
        [JsonProperty("pos")]
        public int Pos { get; set; }
    }
}