using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Processors.Refiner
{
    /// <summary>
    /// One audio clip sent to the AI. In a Start round only the start differs between clips,
    /// in an End round only the end differs.
    /// </summary>
    public class TimingClipOption
    {
        /// <summary>Name of the field sent to the AI: Audio_Max, Audio_S1.., Audio_E1..</summary>
        public string FieldName { get; set; }

        /// <summary>Option id the AI answers with: Max, S1.., E1..</summary>
        public string OptionId { get; set; }

        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }

        /// <summary>Audio put BEFORE [Start, End] (in order): silence for the AI to settle in (see TimingRefinerSettings.StartClipPadding).</summary>
        public List<(TimeSpan From, TimeSpan To)> LeadingPieces { get; } = new List<(TimeSpan From, TimeSpan To)>();

        /// <summary>Audio put AFTER [Start, End] (in order): silence for the AI to finish hearing (see TimingRefinerSettings.EndClipPadding).</summary>
        public List<(TimeSpan From, TimeSpan To)> TrailingPieces { get; } = new List<(TimeSpan From, TimeSpan To)>();

        /// <summary>
        /// Spoken markers INSERTED in [Start, End] (the audio after each one is pushed later, nothing is overwritten), in time order:
        /// see <see cref="SpokenMarkers"/> and <see cref="TimingRefineEngineSpokenMarkers"/>.
        /// </summary>
        public List<TimingClipInsert> Inserts { get; } = new List<TimingClipInsert>();

        /// <summary>The clip is not the plain [Start, End] of the audio: it is built from pieces (silence before / after, spoken markers inside).</summary>
        public bool HasPadding => LeadingPieces.Count > 0 || TrailingPieces.Count > 0 || Inserts.Count > 0;

        /// <summary>The length of the audio really sent (the padding and the spoken markers included).</summary>
        public TimeSpan TotalDuration => (End - Start)
            + LeadingPieces.Aggregate(TimeSpan.Zero, (sum, p) => sum + (p.To - p.From))
            + TrailingPieces.Aggregate(TimeSpan.Zero, (sum, p) => sum + (p.To - p.From))
            + Inserts.Aggregate(TimeSpan.Zero, (sum, i) => sum + SpokenMarkers.DurationOf(i.Label));

        /// <summary>Empty without padding / markers. Part of the cache key: the same [Start, End] with another padding or other markers is another audio.</summary>
        public string PaddingSignature =>
            string.Concat(LeadingPieces.Select(p => $"<{p.From:hh\\:mm\\:ss\\.fff}-{p.To:hh\\:mm\\:ss\\.fff}"))
            + string.Concat(TrailingPieces.Select(p => $">{p.From:hh\\:mm\\:ss\\.fff}-{p.To:hh\\:mm\\:ss\\.fff}"))
            + string.Concat(Inserts.Select(i => $"+{i.Label}@{i.At:hh\\:mm\\:ss\\.fff}"));
    }

    /// <summary>A spoken marker inserted in a clip: its label (a word of <see cref="SpokenMarkers.Labels"/>) at a time of the audio.</summary>
    public class TimingClipInsert
    {
        public TimeSpan At { get; set; }
        public string Label { get; set; }
    }
}
