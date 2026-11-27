using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    [ConfigDoc("ONE AI request per subtitle with ONE clip (the snapped Start to the snapped End) and the text: does the clip contain the text? " +
        "(HeardText when it differs, Inaudible when nothing). The timings are kept whatever the answer: a problem is only flagged.")]
    public class TimingRefineEngineVerify : TimingRefineEngine
    {
        [ConfigDoc("The options of the request (prompt, batching, the clip): it must produce \"Verdict\". Defined in the main configuration.")]
        [JsonProperty(Order = 1)]
        public AIOptions InnerOptions { get; set; }

        [ConfigDoc("The clip starts with at least this much silence (the AI needs a moment to settle in): the silence just before the clip first, the rest taken from the nearest other silence. Zero = no padding.")]
        [JsonProperty(Order = 2)]
        public TimeSpan StartClipPadding { get; set; } = TimeSpan.Zero;

        public override bool UsesAI => true;

        public override IEnumerable<AIOptions> InnerOptionsUsed => new[] { this.InnerOptions };

        public override void Validate()
        {
            if (this.InnerOptions == null)
                throw new InvalidOperationException("The refine engine 'Verify' needs 'InnerOptions' (the options of its request).");
            if (this.StartClipPadding < TimeSpan.Zero)
                throw new InvalidOperationException("The refine engine 'Verify': 'StartClipPadding' can't be negative.");
        }

        public override IReadOnlyList<TimingRefinePass> CreatePasses(TimingRefiner refiner)
            => new TimingRefinePass[] { new VerifyPass(this, refiner) };

        /// <summary>The single request: asked as a Start request (the text of the previous subtitle is the possible bleed).</summary>
        private class VerifyPass : TimingRefinePass
        {
            private readonly TimingRefineEngineVerify r_engine;

            public VerifyPass(TimingRefineEngineVerify engine, TimingRefiner refiner)
                : base(engine, refiner, "Verify", BoundarySide.Start, engine.InnerOptions, TimingMeta.Verdict)
            {
                r_engine = engine;
            }

            public override void PrepareNextRound()
            {
                var candidates = this.Refiner.Candidates;
                for (int i = 0; i < candidates.Count; i++)
                {
                    var cand = candidates[i];
                    cand.CurrentRound = null;
                    if (cand.Start.Locked || cand.Start.RetryCount >= MaxAnswersPerBoundary)
                        continue;

                    SetBleedTexts(this.Refiner, i, BoundarySide.Start);
                    cand.CurrentRound = BuildRound(cand);
                }
            }

            /// <summary>One clip, exactly the snapped Start to the snapped End, and the text.</summary>
            private TimingRound BuildRound(TimingCandidate cand)
            {
                const string optionId = "Verify";
                var round = new TimingRound
                {
                    Side = BoundarySide.Start,
                    RoundNumber = 1,
                    Center = cand.Start.Current,
                    ClipAnchor = cand.End.Current,
                    TextSent = cand.OriginalText,
                };
                round.Options[optionId] = cand.Start.Current;
                var clip = AddClip(this.Refiner, round, BoundarySide.Start, optionId, cand.Start.Current, cand.End.Current, r_engine.StartClipPadding);
                clip.FieldName = "AudioClip";
                return round;
            }

            public override void ProcessResults(IEnumerable<TranscribedItem> responseItems)
            {
                foreach (var item in responseItems)
                {
                    var cand = this.Refiner.FindCandidateFor(item.StartTime, c => c.CurrentRound != null && c.CurrentRound.Side == BoundarySide.Start);
                    if (cand == null)
                        continue;

                    // Whatever the answer: the timings are kept (both boundaries locked, no silence trimming)
                    ApplyAnswer(this.Refiner, cand, item, BoundarySide.Start, TimingMeta.Verdict,
                        (c, round) =>
                        {
                            c.Start.Locked = true;
                            c.End.Locked = true;
                        },
                        trimSilence: false);
                }
            }

            public override void End() => LockUnfinished(this.Refiner, BoundarySide.Start);
        }
    }
}
