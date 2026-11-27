using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Processors.Refiner
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

        /// <summary>The engine is not sure of the Start (TimingRefineEngineSpokenMarkers: the runs did not all give the same marker, tight; or a single run).</summary>
        UnsureStart,

        /// <summary>The engine is not sure of the End (same as UnsureStart, or the End was capped by the next Start).</summary>
        UnsureEnd,

        /// <summary>
        /// TimingRefineEngineSpokenMarkers: the timing picked put the subtitle inside its neighbour (ex. the AI heard its words in the next line,
        /// or the line is not in the audio): it went back to its snapped timing.
        /// </summary>
        InsideNeighbour,

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
        /// <summary>The start of a review line in the manual-input file ('[REVIEW:Code]' or '[REVIEW:Code:Detail]', then the message).</summary>
        public const string ReviewLinePrefix = "[REVIEW:";

        public ValidationCode Code { get; }
        /// <summary>A sub-kind of the code, or null (ex. MaybePointless: the name of the pattern that matched). Grouped under the code in the Timing editor.</summary>
        public string Detail { get; }
        public string Message { get; }

        public ValidationNote(ValidationCode code, string message, string detail = null)
        {
            Code = code;
            Message = message;
            Detail = CleanDetail(detail);
        }

        /// <summary>A detail is between ':' and ']' in the review line: only letters, digits, '-', '_' and '.' are kept (the others become '_').</summary>
        private static string CleanDetail(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
                return null;
            var chars = detail.Trim().Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_').ToArray();
            return new string(chars);
        }

        /// <summary>The line in the manual-input file, ex. "[REVIEW:MaybePointless:SoGood] Maybe pointless: ...". One line (the message has no line break).</summary>
        public string ToReviewLine()
            => $"{ReviewLinePrefix}{Code}{(Detail == null ? string.Empty : ":" + Detail)}] {(Message ?? string.Empty).Replace("\r", " ").Replace("\n", " ")}";

        public override string ToString() => Detail == null ? $"[{Code}] {Message}" : $"[{Code}:{Detail}] {Message}";
    }
}
