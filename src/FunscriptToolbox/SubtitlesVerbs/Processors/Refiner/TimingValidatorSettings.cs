using Newtonsoft.Json;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Processors.Refiner
{
    /// <summary>
    /// Settings of the validation (see <see cref="TimingValidator"/>): what is checked on the snapped subtitles,
    /// and what is done when something is found (nothing or a review flag).
    /// It needs the terrain of the audio, so it can't be enabled when RefineTiming is not.
    /// </summary>
    public class TimingValidatorSettings
    {
        [ConfigDoc("Checks the snapped subtitles and flags what needs a look ('[REVIEW:Code:Detail] reason' lines, checked one by one in the Review editor). Needs RefineTiming.\n" +
            "false: nothing is checked or flagged at all.")]
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        [ConfigDoc("What to do for each kind of finding (by code): \"Warn\" (the subtitle gets a '[REVIEW:Code] reason' line, to check in the Review editor) or \"Ignore\" (not flagged).\n" +
            "A code not listed is a Warn.",
            Advice = "Look at the codes that end up in the review and set Ignore on any check you don't want.")]
        [JsonProperty(Order = 2)]
        public Dictionary<ValidationCode, ValidationMode> Validations { get; set; } = new Dictionary<ValidationCode, ValidationMode>();

        [ConfigDoc("The thresholds of the checks done on the snapped result (the defaults are made for Japanese). A threshold set to null turns its check off.",
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
        }
    }
}
