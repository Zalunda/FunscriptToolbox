using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class AIEngine
    {
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        public abstract AIResponse Execute(
            SubtitleGeneratorContext context,
            AIRequest request);
    }
}