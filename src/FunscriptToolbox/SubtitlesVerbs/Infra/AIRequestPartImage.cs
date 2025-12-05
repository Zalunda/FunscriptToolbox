namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIRequestPartImage : AIRequestPart
    {
        public override string Modality { get; } = "IMAGE";

        public AIRequestPartImage(AIRequestSection section, string filename, byte[] content)
            : base(section)
        {
            this.FileName = filename;
            this.Content = content;
        }

        public string FileName { get; }
        public byte[] Content { get; }

        public override string ForSimplifiedFullPrompt() => "[Image]";
        public override double Weight => 1.0;
    }
}