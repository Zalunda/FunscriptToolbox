using FunscriptToolbox.Core;
using Newtonsoft.Json;

namespace FunscriptToolbox.RetimerVerbs
{
    [JsonObject(IsReference = false)]
    public class RetimerSyncOffsetFile
    {
        public RetimerSyncOffset[] Offsets { get; set; }
        public FunscriptAudioSignature OriginalVideoSignature { get; set; }
    }
}