using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    public class TimingRound
    {
        public int RoundNumber { get; set; }
        public TimeSpan CenterStart { get; set; }
        public TimeSpan CenterEnd { get; set; }
        public Dictionary<string, TimeSpan> StartOptions { get; set; } = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, TimeSpan> EndOptions { get; set; } = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        public List<TimingClipOption> Clips { get; set; } = new List<TimingClipOption>();

        public string BestStart { get; set; }
        public string BestEnd { get; set; }
        public string HeardText { get; set; }
    }
}
