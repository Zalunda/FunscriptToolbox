using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>Everything we track about ONE boundary (start or end) of a candidate.</summary>
    public class BoundaryState
    {
        /// <summary>Initial snapped boundary.</summary>
        public TimeSpan Init { get; set; }

        /// <summary>Current best / working boundary.</summary>
        public TimeSpan Current { get; set; }

        public bool Locked { get; set; }

        /// <summary>
        /// Already validated (the outer edge of a split subtitle): the boundary never moves.
        /// The AI is still asked in the Start pass (that is where the text comes from), with this boundary as the only option.
        /// </summary>
        public bool Fixed { get; set; }

        /// <summary>Number of AI answers already received for this boundary.</summary>
        public int RetryCount { get; set; }

        public List<BoundaryDipCandidate> Dips { get; } = new List<BoundaryDipCandidate>();
    }
}
