using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>One question asked to the AI about ONE boundary (Start or End) of ONE candidate.</summary>
    public class TimingRound
    {
        /// <summary>The boundary being judged. The other boundary is the anchor of every clip.</summary>
        public BoundarySide Side { get; set; }

        public int RoundNumber { get; set; }

        /// <summary>Current value of the judged boundary when the round was built.</summary>
        public TimeSpan Center { get; set; }

        /// <summary>The fixed end of every clip (Start round: initial End + tail padding; End round: the locked Start).</summary>
        public TimeSpan ClipAnchor { get; set; }

        /// <summary>The text given to the AI as OriginalText (part of the cache key).</summary>
        public string TextSent { get; set; }

        /// <summary>S1..Sn (or E1..En), plus Max.</summary>
        public Dictionary<string, TimeSpan> Options { get; set; } = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

        public List<TimingClipOption> Clips { get; set; } = new List<TimingClipOption>();

        /// <summary>Times of the dips still in play when the round was built (not eliminated yet), plus the offered options. Only used for the trace.</summary>
        public HashSet<TimeSpan> DipTimesAtStart { get; set; } = new HashSet<TimeSpan>();

        /// <summary>True when there is nothing to choose from (all the options, Max included, give the same final result).</summary>
        public bool IsTrivial { get; set; }

        /// <summary>True when a previous answer is the only option left to offer (nothing new to compare it to).</summary>
        public bool IsIncumbentOnly { get; set; }

        public string Choice { get; set; }
        public string HeardText { get; set; }
    }
}
