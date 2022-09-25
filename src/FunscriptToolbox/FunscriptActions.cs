using Newtonsoft.Json;

namespace AudioSynchronization
{
    public class FunscriptActions
    {
        [JsonProperty("at")]
        public int At { get; set; }
        [JsonProperty("pos")]
        public int Pos { get; set; }
    }
}