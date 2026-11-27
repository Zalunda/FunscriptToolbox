using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Processors.Refiner
{
    /// <summary>
    /// Settings of the validation (see <see cref="TimingValidator"/>): what is checked on the refined subtitles,
    /// what is done when something is found (nothing, a review flag, or an automatic fix).
    /// It needs the refined timings (and the terrain), so it can't be enabled when the refining is not.
    /// </summary>
    public class TimingValidatorSettings
    {
        /// <summary>The validation that has an automatic fix (the others can only be Warn or Ignore).</summary>
        private const ValidationCode FixableCode = ValidationCode.LongWithSilence;

        [ConfigDoc("Checks the refined subtitles, flags what needs a look ('[REVIEW:Code:Detail] reason' lines, checked one by one in the Review editor) and fixes some problems. Needs RefineTiming.\n" +
            "false: nothing is checked or flagged at all (also what the refining itself flags).")]
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        [ConfigDoc("What to do for each kind of finding (by code): \"Warn\" (the subtitle gets a '[REVIEW:Code] reason' line, to check in the Review editor), \"Ignore\" (not flagged), " +
            "or \"Fix\" (only LongWithSilence: the subtitle is split at its silences, see Checks: MaxSubtitleDuration, MinSilenceToSplit, SplitStrategy; the parts get their text from the AI and are flagged SplitPart).\n" +
            "A code not listed is a Warn.",
            Advice = "Look at the codes that end up in the review: Ignore the ones that fire on almost every subtitle for your engine (ex. with TimingRefineEngineNone, the text is never checked).")]
        [JsonProperty(Order = 2)]
        public Dictionary<ValidationCode, ValidationMode> Validations { get; set; } = new Dictionary<ValidationCode, ValidationMode>();

        [ConfigDoc("The thresholds of the checks done on the final result (the defaults are made for Japanese). A threshold set to null turns its check off.",
            Advice = "Tune them by looking at what ends up in the review.")]
        [JsonProperty(Order = 3)]
        public TimingChecksSettings Checks { get; set; } = new TimingChecksSettings();

        /// <summary>What to do for a code: Ignore for everything when not Enabled.</summary>
        public ValidationMode ModeOf(ValidationCode code)
        {
            if (!Enabled)
                return ValidationMode.Ignore;

            return Validations != null && Validations.TryGetValue(code, out var mode) ? mode : ValidationMode.Warn;
        }

        /// <summary>A clear error when a setting can't work (nothing is checked when not Enabled).</summary>
        public void Validate()
        {
            if (!Enabled)
                return;

            // A bad regex must fail now, not at the end of the work
            Checks?.CreateMaybePointlessRegex();

            foreach (var validation in (Validations ?? new Dictionary<ValidationCode, ValidationMode>())
                .Where(v => v.Value == ValidationMode.Fix && v.Key != FixableCode))
            {
                throw new InvalidOperationException(
                    $"Validation: '{validation.Key}' has no automatic fix, it can only be Warn or Ignore (Fix is for {FixableCode}).");
            }
        }
    }
}
