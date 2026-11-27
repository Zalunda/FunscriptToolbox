using Newtonsoft.Json;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class AIEngine
    {
        [ConfigDoc("false: an AIEngineCollection skips this engine (it goes to the next one of its list).")]
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        [ConfigDoc("The model accepts audio. false: the audio clips are removed from the requests (the AI only gets the text and the images).",
            Advice = "Set it to what the model / the provider really accepts: a request with audio sent to a model that doesn't support it is rejected.")]
        [JsonProperty(Order = 40)]
        public bool SupportAudio { get; set; } = false;
        [ConfigDoc("The model accepts images. false: the screenshots are removed from the requests.")]
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