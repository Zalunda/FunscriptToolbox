namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIRequestPartText : AIRequestPart
    {
        public AIRequestPartText(string content)
        {
            this.Content = content;
        }

        public string Content { get; }
        public override string ForSimplifiedFullPrompt() => this.Content;
    }
}