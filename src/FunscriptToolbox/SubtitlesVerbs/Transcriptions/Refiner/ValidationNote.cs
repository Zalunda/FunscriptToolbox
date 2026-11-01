namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>Everything the refiner can flag on a candidate. The name is what the user writes in the "Validations" setting.</summary>
    public enum ValidationCode
    {
        MissingStart,
        MissingEnd,
        Unfinished,
        TextMismatch,
        Inaudible,
        Overlap,
        BoundariesCrossed,
        NoAnswer,
        UnrecognisedAnswer,

        // Checks done on the final result (thresholds in TimingChecksSettings)
        NoSpeechInside,
        TooShort,
        TooFast,
        TinyGap,
        SpeechRateHigh,
        SpeechRateLow,
        RepeatedText,
        TextLoop,
        MaybePointless,

        // The split of a long subtitle (Fix mode), and what it leaves behind to look at
        LongWithSilence,
        SplitPart,
        EmptyText,

        /// <summary>A title card was detected in the video (its on-screen text has to be read).</summary>
        TitleCard,
    }

    /// <summary>What to do when a check finds something.</summary>
    public enum ValidationMode
    {
        /// <summary>Not flagged at all: the candidate stays "clean" because of it.</summary>
        Ignore,

        /// <summary>The candidate is flagged: it goes in the REVIEW subtitles, with the reason.</summary>
        Warn,

        /// <summary>Apply the automatic fix of that check (only LongWithSilence has one: the subtitle is split at its silences).</summary>
        Fix,
    }

    public class ValidationNote
    {
        public ValidationCode Code { get; }
        public string Message { get; }

        public ValidationNote(ValidationCode code, string message)
        {
            Code = code;
            Message = message;
        }

        public override string ToString() => $"[{Code}] {Message}";
    }
}
