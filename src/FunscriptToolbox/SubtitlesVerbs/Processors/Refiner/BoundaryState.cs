using System;

namespace FunscriptToolbox.SubtitlesVerbs.Processors.Refiner
{
    /// <summary>Everything we track about ONE boundary (start or end) of a candidate.</summary>
    public class BoundaryState
    {
        /// <summary>Initial snapped boundary.</summary>
        public TimeSpan Init { get; set; }

        /// <summary>Current working boundary.</summary>
        public TimeSpan Current { get; set; }

        public bool Locked { get; set; }
    }
}
