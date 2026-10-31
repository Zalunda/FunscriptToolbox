using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>Summary numbers computed by <see cref="TimingRefiner.ComputeStats"/>; the transcriber only prints them.</summary>
    public class TimingRefinerStats
    {
        /// <summary>(substring searched in ValidationNotes, label shown in the summary)</summary>
        public static readonly (string Key, string Label)[] NoteCategories =
        {
            ("Missing Start", "Missing Start (Max limit)"),
            ("Missing End",   "Missing End (Max limit)"),
            ("Mismatch",      "Text Mismatch (in Max)"),
            ("Inaudible",     "Audio Inaudible"),
            ("Overlaps",      "Overlap Warning"),
        };

        public int Total { get; set; }
        public int Clean { get; set; }
        public int Review { get; set; }
        public int Unfinished { get; set; }
        public int ResolvedInOneRound { get; set; }
        public int RefinedWithRetries => Total - ResolvedInOneRound;

        /// <summary>Number of candidates having a note that contains each category key.</summary>
        public Dictionary<string, int> NoteCounts { get; } = new Dictionary<string, int>();

        public double AvgOriginalDuration { get; set; }
        public double AvgFinalDuration { get; set; }
        public double AvgStartDrift { get; set; }
        public double MaxOutwardStartDrift { get; set; }
        public double AvgEndDrift { get; set; }
        public double MaxOutwardEndDrift { get; set; }

        public int Overlaps { get; set; }
        public int Squashed { get; set; }
    }
}
