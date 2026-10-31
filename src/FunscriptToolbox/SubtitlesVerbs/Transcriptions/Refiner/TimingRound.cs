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

        // Per-side accessors (so callers can loop over BoundarySide instead of duplicating Start/End code)
        public TimeSpan CenterFor(BoundarySide side) => side == BoundarySide.Start ? CenterStart : CenterEnd;
        public Dictionary<string, TimeSpan> OptionsFor(BoundarySide side) => side == BoundarySide.Start ? StartOptions : EndOptions;
        public string ChoiceFor(BoundarySide side) => side == BoundarySide.Start ? BestStart : BestEnd;
    }
}
