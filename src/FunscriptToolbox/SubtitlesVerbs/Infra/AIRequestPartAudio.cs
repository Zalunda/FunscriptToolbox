using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIRequestPartAudio : AIRequestPart
    {
        public const int AudioTokensPerSecond = 25; // Gemini has 25 tokens per seconds, I assume something similar for the other vendor.

        public override string Modality { get; } = "AUDIO";
        public override BinaryDataType? AssociatedDataType => BinaryDataType.Audio;

        public AIRequestPartAudio(AIRequestSection section, string filename, byte[] content, TimeSpan duration, TimeSpan extractTime)
            : base(section)
        {
            this.FileName = filename;
            this.Content = content;
            this.Duration = duration;
            this.ExtractTime = extractTime;
        }

        public string FileName { get; }
        public byte[] Content { get; }
        public TimeSpan Duration { get; }
        public TimeSpan ExtractTime { get; }

        public override string ForSimplifiedFullPrompt() => $"[Audio, {Duration.TotalSeconds}]";
        public override long EstimatedRequestBytes => JsonPartOverheadBytes + (((this.Content?.Length ?? 0) + 2) / 3) * 4; // base64
        public override double Units => this.Duration.TotalSeconds;
        public override string UnitName => "seconds";
        public override double EstimatedTokens => this.Units * AudioTokensPerSecond;
    }
}