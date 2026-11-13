using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
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

        public bool HasPadding => LeadingPieces.Count > 0 || TrailingPieces.Count > 0;

        /// <summary>The length of the audio really sent (the padding included).</summary>
        public TimeSpan TotalDuration => (End - Start)
            + LeadingPieces.Aggregate(TimeSpan.Zero, (sum, p) => sum + (p.To - p.From))
            + TrailingPieces.Aggregate(TimeSpan.Zero, (sum, p) => sum + (p.To - p.From));

        /// <summary>Empty without padding. Part of the cache key: the same [Start, End] with another padding is another audio.</summary>
        public string PaddingSignature =>
            string.Concat(LeadingPieces.Select(p => $"<{p.From:hh\\:mm\\:ss\\.fff}-{p.To:hh\\:mm\\:ss\\.fff}"))
            + string.Concat(TrailingPieces.Select(p => $">{p.From:hh\\:mm\\:ss\\.fff}-{p.To:hh\\:mm\\:ss\\.fff}"));
    }
}
