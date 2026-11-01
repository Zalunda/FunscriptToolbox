using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>A change the validator wants to make to the list of subtitles (the refining is then done again, the cache makes the unchanged ones free).</summary>
    public class TimingEdit
    {
        /// <summary>The candidate that is replaced by the spans.</summary>
        public TimingCandidate Replaces { get; set; }

        public TimingOrigin Origin { get; set; }

        /// <summary>The new subtitles, without text.</summary>
        public List<(TimeSpan Start, TimeSpan End)> Spans { get; } = new List<(TimeSpan Start, TimeSpan End)>();
    }
}
