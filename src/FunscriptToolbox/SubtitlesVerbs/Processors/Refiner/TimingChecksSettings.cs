using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Processors.Refiner
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
        [ConfigDoc("TooShort: a subtitle shorter than this. null: not checked.")]
        public TimeSpan? MinDuration { get; set; } = TimeSpan.FromSeconds(0.3);

        [ConfigDoc("TooFast: more letters / digits per second of subtitle than this (too fast to read). null: not checked.")]
        public double? MaxCharsPerSecond { get; set; } = 15;

        [ConfigDoc("TinyGap: a gap (greater than zero) between two subtitles smaller than this. null: not checked.")]
        public TimeSpan? MinGap { get; set; } = TimeSpan.FromSeconds(0.05);

        [ConfigDoc("SpeechRateHigh: more letters / digits per second of speech (according to the terrain) inside the subtitle than this: the text is too long for the audio (a part of the speech is outside the subtitle?). null: not checked.")]
        public double? MaxCharsPerSpeechSecond { get; set; } = 14;

        [ConfigDoc("SpeechRateLow: fewer letters / digits per second of speech (according to the terrain) inside the subtitle than this: some text is missing (or the subtitle is too wide). null: not checked.")]
        public double? MinCharsPerSpeechSecond { get; set; } = 1.0;

        [ConfigDoc("LongWithSilence: a subtitle longer than this that has a silence (see MinSilenceToSplit) inside. null: not checked.")]
        public TimeSpan? MaxSubtitleDuration { get; set; } = TimeSpan.FromSeconds(8);

        [ConfigDoc("LongWithSilence: a silence of at least this duration between two speeches is a place where the subtitle can be split.")]
        public TimeSpan MinSilenceToSplit { get; set; } = TimeSpan.FromSeconds(1);

        [ConfigDoc("How LongWithSilence is fixed (when it's set to \"Fix\" in Validations): UnderMaxDuration (only as much as needed to get every part under MaxSubtitleDuration, each time at the silence nearest the middle) " +
            "or EverySilence (at every silence of at least MinSilenceToSplit).")]
        public SplitStrategy SplitStrategy { get; set; } = SplitStrategy.UnderMaxDuration;

        [ConfigDoc("TextMismatch: the text the AI heard is a mismatch only when its similarity (0 to 1) with the text it was given is below this. null: any difference is a mismatch.")]
        public double? TextMismatchMinSimilarity { get; set; } = 0.8;

        [ConfigDoc("RepeatedText: two close subtitles whose texts have a similarity (0 to 1) of at least this are a repetition (often a transcription error). null: not checked.")]
        public double? RepeatedTextMinSimilarity { get; set; } = 0.9;

        [ConfigDoc("RepeatedText: texts shorter than this (letters / digits) are never a repetition (short answers and moans repeat legitimately).")]
        public int RepeatedTextMinChars { get; set; } = 8;

        [ConfigDoc("MaybePointless: a subtitle made only of these words (one or several, in any order) may not be worth a subtitle (ex. moans, 'kimochii' said over and over). " +
            "Each pattern: { \"Name\", \"Pattern\" }, a regular expression on the text as it is (not case sensitive; katakana and hiragana are different characters). " +
            "Each pattern takes its own trailing spaces / punctuation, so the patterns can follow each other. " +
            "No backreferences or lookarounds (the matching is done in linear time: it can never hang).\n" +
            "null or empty: not checked.",
            Advice = "Remove (or comment with //) the patterns of the words you want to keep without a flag.")]
        public MaybePointlessPattern[] MaybePointlessPatterns { get; set; }

        /// <summary>A pattern of MaybePointlessPatterns.</summary>
        public class MaybePointlessPattern : TranscriberAudioFullAI.TranscriptioToIgnorePattern
        {
            [ConfigDoc("true: said with the words of other patterns, this one doesn't count in the name of the warning (ex. \"ああ、きもちいい\" is \"MaybePointless:Kimochii...\", not \"MaybePointless:Combination\"). The subtitle is still flagged.",
                Advice = "true for the moans / breathing / giggles.")]
            public bool IgnoredInCombination { get; set; }

            public MaybePointlessPattern(string name, string pattern, bool ignoredInCombination = false)
                : base(name, pattern)
            {
                this.IgnoredInCombination = ignoredInCombination;
            }
        }

        /// <summary>The patterns that are usable (not null, not empty).</summary>
        private MaybePointlessPattern[] GetMaybePointlessPatterns()
            => (MaybePointlessPatterns ?? Array.Empty<MaybePointlessPattern>())
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Pattern))
                .ToArray();

        /// <summary>
        /// The check for MaybePointless: the whole text must be made of the words of the patterns (null when not done).
        /// </summary>
        /// <exception cref="InvalidOperationException">A pattern is not usable.</exception>
        public Regex CreateMaybePointlessRegex()
        {
            var patterns = GetMaybePointlessPatterns();
            if (patterns.Length == 0)
                return null;

            try
            {
                foreach (var pattern in patterns)
                    _ = pattern.Regex; // each one must be valid by itself (and the error says which one)
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Checks: 'MaybePointlessPatterns' has a pattern that is not valid: {ex.Message}", ex);
            }

            try
            {
                return new Regex(
                    $"^(?:{string.Join("|", patterns.Select(p => $"(?:{p.Pattern})"))})+$",
                    RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
            {
                throw new InvalidOperationException($"Checks: 'MaybePointlessPatterns' can't be used together: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// The name of the pattern that matches the whole text by itself, said once or many times; otherwise, the first pattern
        /// (not IgnoredInCombination) that matches it with only the patterns IgnoredInCombination around it.
        /// Null when the text mixes several patterns that are not ignored.
        /// </summary>
        public string FindMaybePointlessPatternName(string text)
        {
            var patterns = GetMaybePointlessPatterns();
            var alone = patterns.FirstOrDefault(p => IsOnlyMadeOf(text, new[] { p }));
            if (alone != null)
                return alone.Name;

            var ignored = patterns.Where(p => p.IgnoredInCombination).ToArray();
            if (ignored.Length == 0)
                return null;
            return patterns
                .Where(p => !p.IgnoredInCombination)
                .FirstOrDefault(p => IsOnlyMadeOf(text, ignored.Prepend(p)))
                ?.Name;
        }

        private static bool IsOnlyMadeOf(string text, IEnumerable<MaybePointlessPattern> patterns)
            => new Regex(
                $"^(?:{string.Join("|", patterns.Select(p => $"(?:{p.Pattern})"))})+$",
                RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).IsMatch(text);
    }
}
