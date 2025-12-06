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
        public override double Units => 1;
        public override string UnitName => "images";
        public override double EstimatedTokens => 120.0 * this.Units; // seem to go from 70 to 130 tokens per image for Gemini, I'll assume that it's similar for the other vendor.
    }
}