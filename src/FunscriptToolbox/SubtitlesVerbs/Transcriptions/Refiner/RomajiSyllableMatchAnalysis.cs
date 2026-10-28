using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>Result of comparing the original text with what the AI heard, per side.</summary>
    public class RomajiSyllableMatchAnalysis
    {
        public bool IsMismatch { get; set; }
        public double Similarity { get; set; }

        public SideState StartState { get; set; }
        public SideState EndState { get; set; }

        // Syllables of the original not heard before the first aligned one / after the last aligned one.
        public int MissingStart { get; set; }
        public int MissingEnd { get; set; }

        // Syllables heard before the first aligned one / after the last aligned one.
        public int ExtraStart { get; set; }
        public int ExtraEnd { get; set; }

        public SideState State(BoundarySide side) => side == BoundarySide.Start ? StartState : EndState;
        public int Missing(BoundarySide side) => side == BoundarySide.Start ? MissingStart : MissingEnd;
        public int Extra(BoundarySide side) => side == BoundarySide.Start ? ExtraStart : ExtraEnd;

        /// <summary>True when the clip does not cut speech on that side (Ok, Extra or Substituted).</summary>
        public bool Covers(BoundarySide side) => !IsMismatch && State(side) != SideState.Missing;

        [JsonIgnore]
        public bool CoversAll => Covers(BoundarySide.Start) && Covers(BoundarySide.End);

        public string ToTraceString()
        {
            if (IsMismatch)
                return $"Mismatch {Similarity:0.00}";
            return $"S={SideToString(StartState, MissingStart, ExtraStart)} E={SideToString(EndState, MissingEnd, ExtraEnd)} {Similarity:0.00}";
        }

        private static string SideToString(SideState state, int missing, int extra)
        {
            switch (state)
            {
                case SideState.Missing: return $"Miss{missing}";
                case SideState.Extra: return $"Ext{extra}";
                case SideState.Substituted: return $"Sub{missing}";
                default: return "Ok";
            }
        }
    }
}
