using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    [JsonObject(ItemTypeNameHandling = TypeNameHandling.All)]
    public abstract class TranscriberWhisper : Transcriber
    {
        [JsonProperty(Order = 99)]
        public TranscriberTool TranscriberTool { get; set; }

        public TranscriberWhisper()
        {
        }
    }
}