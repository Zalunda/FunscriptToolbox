namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class AIRequestPart
    {
        public abstract string Modality { get; }

        public abstract BinaryDataType? AssociatedDataType { get; }

        public AIRequestPart(AIRequestSection section) 
        {
            this.Section = section;
        }

        public AIRequestSection Section { get; }
        public abstract string ForSimplifiedFullPrompt();
        public abstract double Units { get; }
        public abstract string UnitName { get; }
        public abstract double EstimatedTokens { get; }
    }
}