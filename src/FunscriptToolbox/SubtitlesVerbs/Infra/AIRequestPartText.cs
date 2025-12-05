namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIRequestPartText : AIRequestPart
    {
        public override string Modality { get; } = "TEXT";

        public AIRequestPartText(AIRequestSection section, string content)
            : base(section)
        {
            this.Content = content;
        }

        public string Content { get; }
        public override string ForSimplifiedFullPrompt() => this.Content;
        public override double Weight => this.Content.Length;
    }
}