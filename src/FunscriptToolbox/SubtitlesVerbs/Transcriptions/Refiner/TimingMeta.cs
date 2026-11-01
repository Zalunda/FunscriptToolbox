namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>Metadata keys exchanged with the AI (single place instead of scattered string literals).</summary>
    public static class TimingMeta
    {
        public const string BestStart = "BestStart";
        public const string BestEnd = "BestEnd";
        public const string HeardText = "HeardText";

        /// <summary>Produced for each node: what the AI heard when it differs from VoiceText, otherwise VoiceText.</summary>
        public const string VoiceTextValidated = "VoiceTextValidated";

        public const string OriginalText = "OriginalText";
        public const string PossibleBleedFromPrev = "PossibleBleedFromPrev";
        public const string PossibleBleedFromNext = "PossibleBleedFromNext";
        public const string TimingTrace = "TimingTrace";
    }
}
