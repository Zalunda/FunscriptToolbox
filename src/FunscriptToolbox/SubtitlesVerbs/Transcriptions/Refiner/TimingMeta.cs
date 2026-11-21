namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>Metadata keys exchanged with the AI (single place instead of scattered string literals).</summary>
    public static class TimingMeta
    {
        public const string BestStart = "BestStart";
        public const string BestEnd = "BestEnd";

        /// <summary>TimingRefineEngineVerify answer: "OK" (the clip is judged, HeardText says what it contains) or "Inaudible".</summary>
        public const string Verdict = "Verdict";
        public const string HeardText = "HeardText";

        /// <summary>
        /// Answer of the tail check (what the audio appended at the end of a subtitle is): Speech, Silence, Breathing, Laughter, Moaning or Noise.
        /// Only "Speech" keeps the end where it is.
        /// </summary>
        public const string TailKind = "TailKind";
        public const string TailKindSpeech = "Speech";

        /// <summary>Produced for each node: what the AI heard when it differs from VoiceText, otherwise VoiceText.</summary>
        public const string VoiceTextValidated = "VoiceTextValidated";

        public const string OriginalText = "OriginalText";
        public const string PossibleBleedFromPrev = "PossibleBleedFromPrev";
        public const string PossibleBleedFromNext = "PossibleBleedFromNext";
        public const string TimingTrace = "TimingTrace";
    }
}
