using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>Summary numbers computed by <see cref="TimingRefiner.ComputeStats"/>; the transcriber only prints them.</summary>
    public class TimingRefinerStats
    {
        /// <summary>(code of the ValidationNotes counted, label shown in the summary)</summary>
        public static readonly (ValidationCode Code, string Label)[] NoteCategories =
        {
            (ValidationCode.MissingStart,       "Missing Start (Max limit)"),
            (ValidationCode.MissingEnd,         "Missing End (Max limit)"),
            (ValidationCode.Unfinished,         "Unfinished (safety limit)"),
            (ValidationCode.TextMismatch,       "Text Mismatch (in Max)"),
            (ValidationCode.Inaudible,          "Audio Inaudible"),
            (ValidationCode.Overlap,            "Overlap Warning"),
            (ValidationCode.BoundariesCrossed,  "Boundaries Crossed"),
            (ValidationCode.NoAnswer,           "No Answer from the AI"),
            (ValidationCode.UnrecognisedAnswer, "Unrecognised Answer"),
            (ValidationCode.NoSpeechInside,     "No Speech Inside"),
            (ValidationCode.TooShort,           "Too Short"),
            (ValidationCode.TooFast,            "Too Fast (chars/s)"),
            (ValidationCode.TinyGap,            "Tiny Gap"),
            (ValidationCode.SpeechRateHigh,     "Text Too Long for the Speech"),
            (ValidationCode.SpeechRateLow,      "Text Too Short for the Speech"),
            (ValidationCode.RepeatedText,       "Same Text as a Nearby Subtitle"),
            (ValidationCode.TextLoop,           "Text Repeating Itself"),
            (ValidationCode.MaybePointless,     "Maybe Pointless (only filler words)"),
            (ValidationCode.LongWithSilence,    "Long with Silence Inside"),
            (ValidationCode.SplitPart,          "Part of a Split Subtitle"),
            (ValidationCode.EmptyText,          "No Text (from the AI)"),
            (ValidationCode.TitleCard,          "Title Card Detected"),
        };

        /// <summary>The subtitles (the title cards are counted apart).</summary>
        public int Total { get; set; }

        public int TitleCards { get; set; }
        public int Clean { get; set; }
        public int Review { get; set; }

        /// <summary>Number of candidates having a note with each code.</summary>
        public Dictionary<ValidationCode, int> NoteCounts { get; } = new Dictionary<ValidationCode, int>();

        public double AvgOriginalDuration { get; set; }
        public double AvgFinalDuration { get; set; }
        public double AvgStartDrift { get; set; }
        public double MaxOutwardStartDrift { get; set; }
        public double AvgEndDrift { get; set; }
        public double MaxOutwardEndDrift { get; set; }

        public int Overlaps { get; set; }
        public int Squashed { get; set; }
    }
}
