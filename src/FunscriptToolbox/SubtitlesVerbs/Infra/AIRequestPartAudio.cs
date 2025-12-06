using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIRequestPartAudio : AIRequestPart
    {
        public override string Modality { get; } = "AUDIO";

        public AIRequestPartAudio(AIRequestSection section, string filename, byte[] content, TimeSpan duration)
            : base(section)
        {
            this.FileName = filename;
            this.Content = content;
            this.Duration = duration;
        }

        public string FileName { get; }
        public byte[] Content { get; }
        public TimeSpan Duration { get; }

        public override string ForSimplifiedFullPrompt() => $"[Audio, {Duration.TotalSeconds}]";
        public override double Units => this.Duration.TotalSeconds;
        public override string UnitName => "seconds";
        public override double EstimatedTokens => this.Units * 32; // 32 tokens per seconds for Gemini, I'll assume that it's similar for the other vendor.
    }
}