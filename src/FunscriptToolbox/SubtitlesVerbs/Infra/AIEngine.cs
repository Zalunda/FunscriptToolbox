using Newtonsoft.Json;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class AIEngine
    {
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        [JsonProperty(Order = 40)]
        public bool SupportAudio { get; set; } = false;
        [JsonProperty(Order = 41)]
        public bool SupportImage { get; set; } = false;

        public AIResponse Execute(
            SubtitleGeneratorContext context,
            AIRequest request,
            AIEngineConfigOverride configOverride)
        {
            return Execute(context, new[] { request }, configOverride).First();
        }

        public abstract AIResponse[] Execute(
            SubtitleGeneratorContext context,
            AIRequest[] requests,
            AIEngineConfigOverride configOverride);

        protected bool IsSupported(AIRequestPart part)
        {
            return part.AssociatedDataType switch
            {
                BinaryDataType.Audio => this.SupportAudio,
                BinaryDataType.Image => this.SupportImage,
                _ => true
            };
        }
    }
}