using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIRequestPartCollection : List<AIRequestPart>
    {
        public void AddText(AIRequestSection section, string text) => this.Add(new AIRequestPartText(section, text));
    }
}