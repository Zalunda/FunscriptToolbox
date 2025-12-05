namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class AIRequestPart
    {
        public abstract string Modality { get; }

        public AIRequestPart(AIRequestSection section) 
        {
            this.Section = section;
        }

        public AIRequestSection Section { get; }
        public abstract string ForSimplifiedFullPrompt();

        public abstract double Weight { get; }
    }
}