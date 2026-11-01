using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    public enum SplitStrategy
    {
        /// <summary>Split only as much as needed to get every part under MaxSubtitleDuration, each time at the silence nearest the middle.</summary>
        UnderMaxDuration,

        /// <summary>Split at every silence of at least MinSilenceToSplit.</summary>
        EverySilence,
    }

    /// <summary>
    /// Thresholds of the checks done on the refined subtitles. The defaults are meant for Japanese
    /// (one character = a kana or a kanji, spoken at about 5 to 10 characters per second of speech).
    /// Set a threshold to null to turn its check off.
    /// (What happens when a check finds something is set per code in the "Validations" setting.)
    /// </summary>
    public class TimingChecksSettings
    {
        /// <summary>TooShort: a subtitle shorter than this.</summary>
        public TimeSpan? MinDuration { get; set; } = TimeSpan.FromSeconds(0.3);

        /// <summary>TooFast: more letters/digits per second of subtitle duration than this.</summary>
        public double? MaxCharsPerSecond { get; set; } = 15;

        /// <summary>TinyGap: a gap (greater than zero) between two subtitles smaller than this.</summary>
        public TimeSpan? MinGap { get; set; } = TimeSpan.FromSeconds(0.05);

        /// <summary>SpeechRateHigh: more letters/digits per second of speech (terrain) inside the subtitle than this: text too long for the audio.</summary>
        public double? MaxCharsPerSpeechSecond { get; set; } = 14;

        /// <summary>SpeechRateLow: fewer letters/digits per second of speech (terrain) inside the subtitle than this: text missing.</summary>
        public double? MinCharsPerSpeechSecond { get; set; } = 1.0;

        /// <summary>LongWithSilence: a subtitle longer than this that has a silence (see MinSilenceToSplit) inside.</summary>
        public TimeSpan? MaxSubtitleDuration { get; set; } = TimeSpan.FromSeconds(8);

        /// <summary>LongWithSilence: a silence of at least this much between two speeches is a place where the subtitle can be split.</summary>
        public TimeSpan MinSilenceToSplit { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>How LongWithSilence is fixed (when its validation is set to Fix).</summary>
        public SplitStrategy SplitStrategy { get; set; } = SplitStrategy.UnderMaxDuration;

        /// <summary>
        /// TextMismatch: the text the AI heard is a mismatch only when its similarity (0 to 1, see <see cref="TextSimilarity"/>)
        /// with the text it was given is below this. Null: any difference is a mismatch.
        /// </summary>
        public double? TextMismatchMinSimilarity { get; set; } = 0.8;

        /// <summary>RepeatedText: two close subtitles whose similarity is at least this are a repetition (see also RepeatedTextMinChars).</summary>
        public double? RepeatedTextMinSimilarity { get; set; } = 0.9;

        /// <summary>RepeatedText: texts shorter than this (letters/digits) are never a repetition (short answers and moans repeat legitimately).</summary>
        public int RepeatedTextMinChars { get; set; } = 8;
    }
}
