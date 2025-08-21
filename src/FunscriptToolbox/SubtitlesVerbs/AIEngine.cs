using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public abstract class AIEngine
    {
        public abstract void Execute(
            SubtitleGeneratorContext context,
            IEnumerable<AIRequest> requests);
    }
}