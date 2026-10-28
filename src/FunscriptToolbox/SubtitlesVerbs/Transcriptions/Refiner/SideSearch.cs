using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>Search state of one boundary (start or end) of a candidate.</summary>
    public class SideSearch
    {
        // Tightest boundary known to cover the speech on that side.
        public TimeSpan? Good { get; set; }
        public bool IsLocked { get; set; }
    }
}
