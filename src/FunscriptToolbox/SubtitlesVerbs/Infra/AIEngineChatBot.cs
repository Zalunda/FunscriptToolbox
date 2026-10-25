using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public sealed class AIEngineChatBot : AIEngine
    {
        public override AIResponse[] Execute(
            SubtitleGeneratorContext context,
            AIRequest[] requests,
            AIEngineConfigOverride configOverride = null)
        {
            throw new Exception("Not supported anymore.");
        }
    }
}