using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
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

        /// <summary>false: nothing is checked or flagged, whatever the other settings say.</summary>
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// What to do for each kind of finding: Warn (the node gets a [REVIEW] block, and goes in the REVIEW subtitles), Ignore,
        /// or Fix (LongWithSilence only). A code that is not listed is a Warn.
        /// </summary>
        [JsonProperty(Order = 2)]
        public Dictionary<ValidationCode, ValidationMode> Validations { get; set; } = new Dictionary<ValidationCode, ValidationMode>();

        /// <summary>Thresholds of the checks done on the final result. A check without a threshold is not done.</summary>
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
