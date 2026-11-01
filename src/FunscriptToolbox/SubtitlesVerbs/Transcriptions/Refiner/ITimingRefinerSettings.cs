using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>
    /// Settings consumed by <see cref="TimingRefiner"/>.
    /// TranscriberAIRefineTiming implements this directly, so the JSON config stays flat.
    /// </summary>
    public interface ITimingRefinerSettings
    {
        /// <summary>After the AI's final Start answer, move the Start forward over pure silence (according to the terrain).</summary>
        bool TrimSilenceOnStart { get; }
        /// <summary>After the AI's final End answer, move the End back over pure silence (according to the terrain).</summary>
        bool TrimSilenceOnEnd { get; }

        /// <summary>What to do for each kind of finding. A code that is not listed is a Warn.</summary>
        IReadOnlyDictionary<ValidationCode, ValidationMode> Validations { get; }

        /// <summary>Thresholds of the checks done on the final result (a check without a threshold is not done).</summary>
        TimingChecksSettings Checks { get; }

        TimeSpan MaxExpansion { get; }
        TimeSpan BleedDetectionMargin { get; }

        /// <summary>
        /// START pass only: every clip ends this long after the initial End, so the AI can hear the whole line
        /// (and verify the text) while only the beginning differs between the clips.
        /// </summary>
        TimeSpan StartPassTailPadding { get; }

        float ValleyReach { get; }
    }
}
