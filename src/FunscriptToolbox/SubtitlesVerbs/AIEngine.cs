using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public abstract class AIEngine
    {
        public abstract bool Execute(
            SubtitleGeneratorContext context,
            IEnumerable<AIRequest> requests);
    }
}