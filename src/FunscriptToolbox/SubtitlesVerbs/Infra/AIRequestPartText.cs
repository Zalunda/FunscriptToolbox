using System;

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

        internal static double GetEstimatedTokensFromChar(double nbCharsInResponse)
        {
            return nbCharsInResponse / 3.5;
        }

        public override double Units => this.Content.Length;
        public override string UnitName => "chars";
        public override double EstimatedTokens => GetEstimatedTokensFromChar(this.Units);
    }
}