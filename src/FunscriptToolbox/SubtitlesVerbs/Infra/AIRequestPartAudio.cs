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
        public override double Weight => this.Duration.TotalSeconds;
    }
}