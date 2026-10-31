using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>Everything we track about ONE boundary (start or end) of a candidate.</summary>
    public class BoundaryState
    {
        /// <summary>Initial snapped boundary.</summary>
        public TimeSpan Init { get; set; }

        /// <summary>Current best / working boundary.</summary>
        public TimeSpan Current { get; set; }

        /// <summary>The boundary may not move beyond this (set by neighbours).</summary>
        public TimeSpan? HardLimit { get; set; }

        public bool Locked { get; set; }
    }
}
