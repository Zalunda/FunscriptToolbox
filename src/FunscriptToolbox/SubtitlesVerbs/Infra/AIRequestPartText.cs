using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIRequestPartText : AIRequestPart
    {
        public override string Modality { get; } = "TEXT";
        public override BinaryDataType? AssociatedDataType { get; }

        public AIRequestPartText(AIRequestSection section, string content, BinaryDataType? associatedDataType = null)
            : base(section)
        {
            this.Content = content;
            this.AssociatedDataType = associatedDataType;
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