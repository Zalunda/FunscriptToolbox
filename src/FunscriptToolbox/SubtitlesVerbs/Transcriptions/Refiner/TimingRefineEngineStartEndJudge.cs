using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>
    /// After the initial snap: the AI picks the best Start, then the best End, among options built on the terrain (the dips), in TWO
    /// sequential passes, then (optional, EndTail) checks what the chosen Ends appended:
    ///
    ///   1. START pass: every clip of a node ends at the same fixed point (initial End + tail padding);
    ///      only the start differs (Audio_Max, Audio_S1 = tightest .. Audio_Sn = widest).
    ///   2. END pass: the Start is now locked, every clip begins at it;
    ///      only the end differs (Audio_Max, Audio_E1 = tightest .. Audio_En = widest).
    ///
    /// Each question the AI has to answer is therefore about ONE boundary. Several requests per subtitle when there are more dips than
    /// MaxTestsInRequest (binary search around the answers).
    ///
    /// Neighbour handling:
    ///   - Start pass: all Init values are suspect; a start may go back anywhere except onto/before the previous candidate's INITIAL start.
    ///   - Between the passes: every End is capped by the next (now locked) Start.
    /// </summary>
    [ConfigDoc("The AI picks the best Start, then the best End, among options built on the terrain (several AI requests and clips per subtitle), " +
        "then (optional, EndTail) checks what the chosen Ends appended.")]
    public class TimingRefineEngineStartEndJudge : TimingRefineEngine
    {
        private static readonly TimeSpan MinOverlapViableDuration = TimeSpan.FromSeconds(0.1);
        private static readonly TimeSpan TinyOverlapThreshold = TimeSpan.FromMilliseconds(60);
        private const string MaxOptionKey = "Max";

        [ConfigDoc("The options of the requests of the Start pass (prompt, batching, the clips): they must produce \"BestStart\" (and optionally \"HeardText\"). Defined in the main configuration.")]
        [JsonProperty(Order = 1)]
        public AIOptions InnerOptionsStart { get; set; }

        [ConfigDoc("The options of the requests of the End pass: they must produce \"BestEnd\". Defined in the main configuration.")]
        [JsonProperty(Order = 2)]
        public AIOptions InnerOptionsEnd { get; set; }

        [ConfigDoc("How many options are in one request (more options: spread evenly, then narrowed down around the choice of the AI, in other requests).",
            Advice = "Ideally greater than MaxDipsInward + MaxDipsOutward + 1, so that only 1 request is needed per boundary (bigger requests, fewer of them).")]
        [JsonProperty(Order = 3)]
        public int MaxTestsInRequest { get; set; } = 4;

        [ConfigDoc("How many options (dips of the terrain) inside the snapped boundary are tried (fewer = cheaper).")]
        [JsonProperty(Order = 4)]
        public int MaxDipsInward { get; set; } = 100;

        [ConfigDoc("How many options (dips of the terrain) outside the snapped boundary are tried (fewer = cheaper, but a boundary that is further is flagged Missing instead of being found).")]
        [JsonProperty(Order = 5)]
        public int MaxDipsOutward { get; set; } = 100;

        [ConfigDoc("How far from the snapped boundary the options can go.")]
        [JsonProperty(Order = 6)]
        public TimeSpan MaxExpansion { get; set; } = TimeSpan.FromSeconds(2.0);

        [ConfigDoc("Start pass only: every clip ends this long after the initial End, so that the AI hears the whole line (more = more tokens).")]
        [JsonProperty(Order = 7)]
        public TimeSpan StartPassTailPadding { get; set; } = TimeSpan.FromSeconds(0.8);

        [ConfigDoc("Start pass only: every clip starts with at least this much silence (the AI needs a moment to settle in): the silence just before the clip first, " +
            "the rest taken from the nearest other silence of the audio and spliced in front (never digital silence: the AI could react differently to it). Zero = no padding.")]
        [JsonProperty(Order = 8)]
        public TimeSpan StartClipPadding { get; set; } = TimeSpan.Zero;

        [ConfigDoc("End pass only: every clip ends with at least this much silence (the AI understands a sound a moment after hearing it: it needs to hear what follows " +
            "the end to recognize the last syllable). Same rules as StartClipPadding. Zero = no padding.")]
        [JsonProperty(Order = 9)]
        public TimeSpan EndClipPadding { get; set; } = TimeSpan.Zero;

        [ConfigDoc("After the End pass: when the End chosen by the AI is at least EndTail.MinExtra after the closest option before it, one more request (no text) asks " +
            "what that extra audio is: Speech, Laughter, Moaning, Breathing, Noise or Silence. Anything but Speech moves the End back to that option. null: not done.")]
        [JsonProperty(Order = 10)]
        public TimingRefineEndTailSettings EndTail { get; set; }

        public override bool UsesAI => true;
        public override bool SupportsSimulations => true;

        public override IEnumerable<AIOptions> InnerOptionsUsed
            => new[] { this.InnerOptionsStart, this.InnerOptionsEnd }
                .Concat(this.EndTail != null ? new[] { this.EndTail.InnerOptions } : Array.Empty<AIOptions>());

        private TimeSpan TailPadding => this.StartPassTailPadding < TimeSpan.Zero ? TimeSpan.Zero : this.StartPassTailPadding;

        public override void Validate()
        {
            if (this.InnerOptionsStart == null || this.InnerOptionsEnd == null)
                throw new InvalidOperationException("The refine engine 'StartEndJudge' needs 'InnerOptionsStart' and 'InnerOptionsEnd' (the options of its requests).");
            if (this.StartClipPadding < TimeSpan.Zero || this.EndClipPadding < TimeSpan.Zero)
                throw new InvalidOperationException("The refine engine 'StartEndJudge': 'StartClipPadding' and 'EndClipPadding' can't be negative.");
            this.EndTail?.Validate();

            // The padded clips are built by the refiner itself: the expansion of the extractor would be ignored
            foreach (var (side, padding, options) in new[] { (BoundarySide.Start, StartClipPadding, InnerOptionsStart), (BoundarySide.End, EndClipPadding, InnerOptionsEnd) })
            {
                if (padding <= TimeSpan.Zero)
                    continue;

                foreach (var extractor in options.BinaryDataExtractors?.OfType<BinaryDataExtractorAudio>() ?? Enumerable.Empty<BinaryDataExtractorAudio>())
                {
                    if (extractor.ExpandStart != TimeSpan.Zero || extractor.ExpandEnd != TimeSpan.Zero || extractor.FillGapSmallerThen != TimeSpan.Zero)
                        throw new InvalidOperationException(
                            $"The refine engine 'StartEndJudge' has a clip padding for the {side} pass, but its audio extractor '{extractor.OutputFieldName}' has ExpandStart, ExpandEnd or FillGapSmallerThen set: " +
                            "they can't be combined (set them to 0 and use StartClipPadding / EndClipPadding).");
                }
            }
        }

        public override IReadOnlyList<TimingRefinePass> CreatePasses(TimingRefiner refiner)
        {
            var passes = new List<TimingRefinePass>
            {
                new SidePass(this, refiner, BoundarySide.Start),
                new SidePass(this, refiner, BoundarySide.End)
            };
            if (this.EndTail != null)
                passes.Add(new TailPass(this, refiner));
            return passes;
        }

        // =====================================================================
        // The Start / End passes
        // =====================================================================

        private class SidePass : TimingRefinePass
        {
            private readonly TimingRefineEngineStartEndJudge r_engine;

            public SidePass(TimingRefineEngineStartEndJudge engine, TimingRefiner refiner, BoundarySide side)
                : base(engine, refiner, side.ToString(), side,
                      side == BoundarySide.Start ? engine.InnerOptionsStart : engine.InnerOptionsEnd,
                      side == BoundarySide.Start ? TimingMeta.BestStart : TimingMeta.BestEnd)
            {
                r_engine = engine;
            }

            private List<TimingCandidate> Candidates => this.Refiner.Candidates;

            /// <summary>
            /// Start: builds the Start dips and forbids starts that would go back into the previous candidate's initial end.
            /// End:   builds the End dips (the Starts are locked now) and caps every End by the next Start.
            /// </summary>
            public override void Begin()
            {
                for (int i = 0; i < Candidates.Count; i++)
                    InitializeDips(i);

                if (this.Side == BoundarySide.Start)
                    ApplyStartFloors();
                else
                    CapEndsByNextStarts();
            }

            /// <summary>
            /// Prepares the next round on every candidate whose boundary is still open (CurrentRound stays null for locked / exhausted
            /// candidates). In the End pass, a boundary with a single possible option is locked without asking the AI.
            /// (The Start pass always asks: it is also the pass that verifies the text and detects inaudible nodes.)
            /// </summary>
            public override void PrepareNextRound()
            {
                var side = this.Side;
                for (int i = 0; i < Candidates.Count; i++)
                {
                    var cand = Candidates[i];
                    var boundary = cand[side];

                    cand.CurrentRound = null;
                    if (boundary.Locked || boundary.RetryCount >= MaxAnswersPerBoundary)
                        continue;

                    SetBleedTexts(this.Refiner, i, side);

                    var round = BuildRound(i);

                    if ((side == BoundarySide.End && round.IsTrivial) || round.IsIncumbentOnly)
                    {
                        LockWithoutAsking(cand, round);
                        continue;
                    }

                    cand.CurrentRound = round;
                }
            }

            public override void ProcessResults(IEnumerable<TimedItemWithMetadata> responseItems)
            {
                var side = this.Side;
                foreach (var item in responseItems)
                {
                    var cand = this.Refiner.FindCandidateFor(item.StartTime, c => c.CurrentRound != null && c.CurrentRound.Side == side);
                    if (cand != null)
                        ApplyAnswer(this.Refiner, cand, item, side, this.MetadataProduced, ApplySideChoice, trimSilence: true);
                }
            }

            /// <summary>Whatever is still open on this side is locked and flagged, so the next pass can rely on it.</summary>
            public override void End() => LockUnfinished(this.Refiner, this.Side);

            // ---------------------------------------------------------------------
            // Dips and neighbours
            // ---------------------------------------------------------------------

            private void InitializeDips(int index)
            {
                var side = this.Side;
                var cand = Candidates[index];
                var boundary = cand[side];
                boundary.OutwardLimit = null;
                var (_, durationAnchor) = AnchorsFor(index);
                var max = ComputeMax(cand, boundary.Current, durationAnchor);
                var inwardLimit = InwardLimit(durationAnchor);

                boundary.Dips.Clear();
                TimeSpan? dropped;
                foreach (var t in LimitDipsAround(this.Refiner.TerrainMapping.GetDips(side, inwardLimit, max), boundary.Current, side, r_engine.MaxDipsInward, r_engine.MaxDipsOutward, out dropped))
                    boundary.Dips.Add(new BoundaryDipCandidate(t));
                boundary.OutwardLimit = dropped;

                // Ensure the current snapped boundary is present in the list
                EnsureDipPresent(boundary.Dips, boundary.Current);
            }

            private void ApplyStartFloors()
            {
                for (int i = 1; i < Candidates.Count; i++)
                {
                    var prev = Candidates[i - 1];
                    var cand = Candidates[i];

                    // All Init values are suspect, so the only limit is the previous node's own start:
                    // a start may go back anywhere inside the previous node, but not onto/before its Start-init
                    // (that would swallow the previous node entirely).
                    // The candidate's own initial start is never forbidden.
                    foreach (var d in cand.Start.Dips)
                    {
                        if (d.Time <= prev.Start.Init && d.Time < cand.Start.Init)
                            d.State = DipCandidateState.HardLimit;
                    }
                }
            }

            /// <summary>The Starts are locked: no End may go beyond the start of the next candidate.</summary>
            private void CapEndsByNextStarts()
            {
                for (int i = 0; i < Candidates.Count - 1; i++)
                {
                    var cur = Candidates[i];
                    var next = Candidates[i + 1];
                    var cap = next.Start.Current;

                    if (cap <= cur.Start.Current + MinOverlapViableDuration)
                    {
                        // Cannot cap without squashing cur: leave it and flag the overlap if there is one
                        if (cur.End.Current > cap)
                        {
                            this.Refiner.Flag(cur, ValidationCode.Overlap, "Overlaps with adjacent subtitle");
                            this.Refiner.Flag(next, ValidationCode.Overlap, "Overlaps with adjacent subtitle");
                        }
                        continue;
                    }

                    if (cur.End.Current > cap)
                    {
                        var prevEnd = cur.End.Current;
                        var overlap = prevEnd - cap;

                        cur.End.Current = overlap <= TinyOverlapThreshold ? cap : FindEndAtOrBefore(cur, cap);
                        cur.Trace("CapE", cur.Start.Current, prevEnd, $"Overruled by next Start ({cap:mm\\:ss\\.fff})");
                    }

                    SetHardLimit(cur, BoundarySide.End, cap, ensurePresent: true);
                }
            }

            private TimeSpan FindEndAtOrBefore(TimingCandidate cand, TimeSpan cap)
            {
                var minEnd = cand.Start.Current + MinOverlapViableDuration;

                var dip = this.Refiner.TerrainMapping.FindDip(cap, BoundarySide.End, MoveDirection.Inward, new DipSearch
                {
                    Inclusive = true,
                    StopAtSpeechEdge = true,
                    Limit = minEnd
                });
                if (dip.HasValue && dip.Value <= cap && dip.Value > minEnd)
                    return dip.Value;

                if (cand.End.Init <= cap && cand.End.Init > minEnd)
                    return cand.End.Init;

                return cap;
            }

            // ---------------------------------------------------------------------
            // The round of a candidate
            // ---------------------------------------------------------------------

            private void LockWithoutAsking(TimingCandidate cand, TimingRound round)
            {
                var boundary = cand[round.Side];
                var prevStart = cand.Start.Current;
                var prevEnd = cand.End.Current;

                // All the options give the same final result: keep the incumbent if there is one, otherwise the tightest option
                var onlyOption = round.IsIncumbentOnly ? round.Center : round.Options[OptionKey(round.Side, 1)];
                boundary.Current = onlyOption;
                boundary.Locked = true;
                MarkDipSelected(cand, round.Side, onlyOption);
                cand.Trace("Auto", prevStart, prevEnd, $"No {round.Side} choice to make, kept");

                this.Refiner.TrimSilence(cand, round.Side);
            }

            /// <summary>
            /// clipAnchor     = the fixed end of every clip.
            /// durationAnchor = the other boundary, used to keep the dips from squashing the subtitle.
            /// Start side: clips run [option .. End + tail padding]. End side: clips run [locked Start .. option].
            /// </summary>
            private (TimeSpan clipAnchor, TimeSpan durationAnchor) AnchorsFor(int index)
            {
                var cand = Candidates[index];
                if (this.Side == BoundarySide.End)
                    return (cand.Start.Current, cand.Start.Current);

                var end = cand.End.Current;
                var tail = end + r_engine.TailPadding;

                // Never let the tail run into the first sound of the next candidate
                if (index + 1 < Candidates.Count)
                {
                    var nextStart = Candidates[index + 1].Start.Init;
                    if (nextStart > end && nextStart < tail)
                        tail = nextStart;
                }

                var clipEnd = this.Refiner.ClampToAudioBounds(tail);
                if (clipEnd < end)
                    clipEnd = end;

                return (clipEnd, end);
            }

            private TimeSpan InwardLimit(TimeSpan durationAnchor)
                => this.Side == BoundarySide.Start ? durationAnchor - TimingRefiner.MinClipDuration : durationAnchor + TimingRefiner.MinClipDuration;

            private TimingRound BuildRound(int index)
            {
                var side = this.Side;
                var cand = Candidates[index];
                var boundary = cand[side];
                var center = boundary.Current;
                var (clipAnchor, durationAnchor) = AnchorsFor(index);
                var inwardLimit = InwardLimit(durationAnchor);

                var round = new TimingRound
                {
                    Side = side,
                    RoundNumber = boundary.RetryCount + 1,
                    Center = center,
                    ClipAnchor = clipAnchor,
                    // The Start pass judges the raw draft; the End pass benefits from what the Start pass heard
                    TextSent = side == BoundarySide.Start ? cand.OriginalText : cand.TextForAI
                };

                var max = ComputeMax(cand, center, durationAnchor);
                var active = SelectActiveDips(cand, center, inwardLimit, max);

                // An already validated boundary has nothing to choose from (the Start pass still asks, for the text)
                if (boundary.Fixed)
                {
                    max = center;
                    active = new List<TimeSpan> { center };
                }

                // Neutral labels, ordered from tightest (innermost) to widest (outermost)
                var ordered = LabelOptions(side, active, round);

                // Clamp Max to the widest option if the terrain beyond it is only silence
                max = TrimSilentMargin(max, ordered[ordered.Count - 1]);
                round.Options[MaxOptionKey] = max;

                AddClips(round, ordered, max, clipAnchor);

                // For the trace: what was still in play at the start of this round (hard limits included), plus what was offered (Max included)
                foreach (var d in boundary.Dips.Where(d => d.State != DipCandidateState.Eliminated))
                    round.DipTimesAtStart.Add(d.Time);
                foreach (var t in round.Options.Values)
                    round.DipTimesAtStart.Add(t);

                // All the options are sent as they are, but a request is useless when they all give the same final result
                // (after the silence trimming): nothing to choose from.
                var finalOfTightest = this.Refiner.TrimmedTime(cand, side, ordered[0]);
                bool allSameFinal = ordered.All(t => this.Refiner.TrimmedTime(cand, side, t) == finalOfTightest);

                round.IsTrivial = allSameFinal
                    && this.Refiner.TrimmedTime(cand, side, max) == finalOfTightest;

                // A refinement round (the previous answer is the incumbent) with nothing left that could change the result
                round.IsIncumbentOnly = allSameFinal
                    && boundary.Dips.Any(d => d.Time == center && d.State == DipCandidateState.Selected);

                return round;
            }

            private List<TimeSpan> SelectActiveDips(
                TimingCandidate cand,
                TimeSpan center,
                TimeSpan inwardDurationLimit,
                TimeSpan outwardMax)
            {
                var side = this.Side;
                var boundary = cand[side];
                if (boundary.Locked)
                    return new List<TimeSpan> { center };

                bool isStart = side == BoundarySide.Start;
                Func<TimeSpan, bool> inRange = isStart
                    ? t => t <= inwardDurationLimit && t >= outwardMax
                    : t => t >= inwardDurationLimit && t <= outwardMax;

                var available = boundary.Dips
                    .Where(d => d.State != DipCandidateState.Eliminated && d.State != DipCandidateState.HardLimit && inRange(d.Time))
                    .Select(d => d.Time)
                    .ToList();

                var inward = available
                    .Where(t => isStart ? t > center : t < center);
                inward = isStart
                    ? inward.OrderBy(t => t)
                    : inward.OrderByDescending(t => t);

                var outward = available
                    .Where(t => isStart ? t < center : t > center);
                outward = isStart
                    ? outward.OrderByDescending(t => t)
                    : outward.OrderBy(t => t);

                // Every dip still in play (center included when it has not been eliminated)
                var pool = inward
                    .Concat(available.Where(t => t == center))
                    .Concat(outward)
                    .Distinct()
                    .OrderBy(t => t)
                    .ToList();

                // Binary-search style: the incumbent (the dip chosen by the previous answer) is always re-offered,
                // the other slots are spread evenly over the dips that are still untested.
                var incumbents = pool
                    .Where(t => boundary.Dips.Any(d => d.Time == t && d.State == DipCandidateState.Selected))
                    .ToList();
                var others = pool.Except(incumbents).ToList();
                var slots = Math.Max(1, r_engine.MaxTestsInRequest - incumbents.Count);

                var selected = incumbents
                    .Concat(SampleEvenly(others, slots))
                    .Distinct()
                    .OrderBy(t => t)
                    .ToList();

                return selected.Count > 0 ? selected : new List<TimeSpan> { center };
            }

            /// <summary>Furthest the boundary may go outward (limited by MaxExpansion, audio bounds and hard limits).</summary>
            private TimeSpan ComputeMax(TimingCandidate cand, TimeSpan center, TimeSpan durationAnchor)
            {
                var side = this.Side;
                var hardLimit = GetHardLimit(cand, side);
                var outwardLimit = cand[side].OutwardLimit; // first dip dropped by MaxDipsOutward (null = none dropped)

                if (side == BoundarySide.Start)
                {
                    var max = this.Refiner.ClampToAudioBounds(center - r_engine.MaxExpansion);
                    if (hardLimit.HasValue && max < hardLimit.Value)
                        max = hardLimit.Value;
                    if (outwardLimit.HasValue && max < outwardLimit.Value)
                        max = outwardLimit.Value;
                    return max;
                }

                var endMax = this.Refiner.ClampToAudioBounds(center + r_engine.MaxExpansion);
                if (hardLimit.HasValue && endMax > hardLimit.Value)
                    endMax = hardLimit.Value;
                if (outwardLimit.HasValue && endMax > outwardLimit.Value)
                    endMax = outwardLimit.Value;
                if (endMax <= durationAnchor)
                    endMax = this.Refiner.ClampToAudioBounds(durationAnchor + TimingRefiner.MinClipDuration);
                return endMax;
            }

            /// <summary>
            /// Registers S1..SK (start) or E1..EK (end) in round.Options and returns the ordered times (index 0 = tightest).
            /// Start: latest start = tightest (S1). End: earliest end = tightest (E1).
            /// </summary>
            private static List<TimeSpan> LabelOptions(BoundarySide side, IEnumerable<TimeSpan> times, TimingRound round)
            {
                var distinct = times.Distinct();
                var ordered = (side == BoundarySide.Start
                        ? distinct.OrderByDescending(t => t)
                        : distinct.OrderBy(t => t)).ToList();

                for (int i = 0; i < ordered.Count; i++)
                    round.Options[OptionKey(side, i + 1)] = ordered[i];
                return ordered;
            }

            private TimeSpan TrimSilentMargin(TimeSpan max, TimeSpan widest)
            {
                var side = this.Side;
                bool beyondWidest = side == BoundarySide.Start ? max < widest : max > widest;
                if (!beyondWidest)
                    return max;

                var (from, to) = side == BoundarySide.Start ? (max, widest) : (widest, max);
                return this.Refiner.TerrainMapping.HasSpeechBetween(from, to) ? max : widest;
            }

            /// <summary>Audio_Max first (the wide reference), then Audio_S1/E1 (tightest) to the widest.</summary>
            private void AddClips(TimingRound round, List<TimeSpan> ordered, TimeSpan max, TimeSpan clipAnchor)
            {
                var side = this.Side;
                var padding = side == BoundarySide.Start ? r_engine.StartClipPadding : r_engine.EndClipPadding;
                AddClip(this.Refiner, round, side, MaxOptionKey, max, clipAnchor, padding);
                for (int i = 0; i < ordered.Count; i++)
                    AddClip(this.Refiner, round, side, OptionKey(side, i + 1), ordered[i], clipAnchor, padding);
            }

            // ---------------------------------------------------------------------
            // The answer
            // ---------------------------------------------------------------------

            private void ApplySideChoice(TimingCandidate cand, TimingRound round)
            {
                var side = this.Side;
                var boundary = cand[side];

                var (time, locked, needsMore) = ResolveBoundaryChoice(cand, round, round.Choice);
                if (boundary.Fixed)
                    (time, locked, needsMore) = (round.Center, true, false);
                boundary.Current = time;
                boundary.Locked = locked;

                if (needsMore && locked)
                {
                    boundary.Locked = true;
                    this.Refiner.Flag(cand, side == BoundarySide.Start ? ValidationCode.MissingStart : ValidationCode.MissingEnd, $"Missing {side} (Max expansion reached)");
                }
            }

            private (TimeSpan resolvedTime, bool isLocked, bool needsMoreExpansion) ResolveBoundaryChoice(
                TimingCandidate cand,
                TimingRound round,
                string rawChoice)
            {
                var side = this.Side;
                var options = round.Options;
                var fallback = round.Center;

                if (string.IsNullOrWhiteSpace(rawChoice))
                {
                    this.Refiner.Flag(cand, ValidationCode.NoAnswer, $"No {side} answer from the AI, kept previous timing");
                    return (fallback, true, false);
                }

                string token = NormalizeToken(rawChoice);

                if (string.Equals(token, MaxOptionKey, StringComparison.OrdinalIgnoreCase))
                {
                    return ResolveMaxChoice(cand, options, fallback);
                }

                if (options.TryGetValue(token, out var resolved))
                {
                    var hasMore = MarkDipsAfterChoice(cand, options, resolved, narrowToWindow: true);
                    return (resolved, !hasMore, false);
                }

                this.Refiner.Flag(cand, ValidationCode.UnrecognisedAnswer, $"Unrecognised {side} answer '{rawChoice}', kept previous timing");
                MarkDipsAfterChoice(cand, options, fallback, narrowToWindow: false);
                return (fallback, true, false);
            }

            /// <summary>
            /// The chosen dip becomes Selected and the other tested dips are eliminated.
            /// With narrowToWindow (binary search), the untested dips beyond the tested neighbours of the choice
            /// are eliminated too: only the ones between those neighbours are still worth testing.
            /// </summary>
            /// <returns>true when untested dips remain (another request is needed to narrow the choice).</returns>
            private bool MarkDipsAfterChoice(
                TimingCandidate cand,
                Dictionary<string, TimeSpan> options,
                TimeSpan resolved,
                bool narrowToWindow)
            {
                var side = this.Side;
                var testedTimes = new HashSet<TimeSpan>(options.Values);

                // Nearest tested neighbours of the choice (null = the choice is the last tested on that side: the window stays open)
                var optionTimes = GetOptionTimes(options);
                TimeSpan? lower = optionTimes.Where(t => t < resolved).Select(t => (TimeSpan?)t).Max();
                TimeSpan? upper = optionTimes.Where(t => t > resolved).Select(t => (TimeSpan?)t).Min();

                // An untested dip that gives the same final result as the choice (after the silence trimming) is not worth a request
                var resolvedFinal = this.Refiner.TrimmedTime(cand, side, resolved);

                bool hasMore = false;
                foreach (var d in cand[side].Dips)
                {
                    if (d.State == DipCandidateState.HardLimit)
                        continue;

                    if (d.Time == resolved)
                    {
                        d.State = DipCandidateState.Selected;
                    }
                    else if (testedTimes.Contains(d.Time))
                    {
                        d.State = DipCandidateState.Eliminated;
                    }
                    else if (narrowToWindow && ((lower.HasValue && d.Time <= lower.Value) || (upper.HasValue && d.Time >= upper.Value)))
                    {
                        d.State = DipCandidateState.Eliminated;
                    }
                    else if (narrowToWindow && d.State == DipCandidateState.Untested && this.Refiner.TrimmedTime(cand, side, d.Time) != resolvedFinal)
                    {
                        hasMore = true;
                    }
                }
                return hasMore;
            }

            /// <summary>
            /// The AI answered "Max". We move the center to the widest candidate option and retry,
            /// marking all dips at or inside the widest candidate as eliminated.
            /// </summary>
            private (TimeSpan, bool, bool) ResolveMaxChoice(
                TimingCandidate cand,
                Dictionary<string, TimeSpan> options,
                TimeSpan fallback)
            {
                var side = this.Side;
                bool isStart = side == BoundarySide.Start;
                var maxOpt = options.TryGetValue(MaxOptionKey, out var m) ? m : fallback;
                var widestC = GetWidestCandidateTime(options, isStart, fallback);

                EliminateDips(cand, side, widestC);

                // 0. Not every dip was offered (MaxTestsInRequest):"Max" only means "wider than the widest OFFERED option".
                //    Untested dips beyond it, still within Max, must be tested before anything can be locked.
                bool untestedBeyond = cand[side].Dips.Any(d =>
                    d.State == DipCandidateState.Untested &&
                    (isStart ? d.Time < widestC && d.Time >= maxOpt : d.Time > widestC && d.Time <= maxOpt));
                if (untestedBeyond)
                    return (widestC, false, true);

                // 1. Max == widest candidate (rest of the zone was silence or clamped): lock cleanly
                if (maxOpt == widestC)
                {
                    MarkDipSelected(cand, side, widestC);
                    return (widestC, true, false);
                }

                // 2. Max is already clamped against a hard limit (neighbour): lock cleanly
                var hardLimit = GetHardLimit(cand, side);
                if (hardLimit.HasValue && (isStart ? maxOpt <= hardLimit.Value : maxOpt >= hardLimit.Value))
                {
                    MarkDipSelected(cand, side, widestC);
                    return (widestC, true, false);
                }

                // 3. Widest did not advance beyond fallback (no dips existed): lock and flag Missing.
                if (widestC == fallback)
                {
                    MarkDipSelected(cand, side, widestC);
                    return (widestC, true, true);
                }

                // 4. MaxDipsOutward left some dips out: Max is the first of them, and the AI says the speech goes on beyond the widest offered option.
                //    That is the limit of the search (one request per boundary): lock on the widest and flag Missing.
                if (cand[side].OutwardLimit.HasValue)
                {
                    MarkDipSelected(cand, side, widestC);
                    return (widestC, true, true);
                }

                return (widestC, false, true);
            }
        }

        private static string OptionPrefix(BoundarySide side) => side == BoundarySide.Start ? "S" : "E";
        private static string OptionKey(BoundarySide side, int index) => $"{OptionPrefix(side)}{index}";

        private static bool IsMaxKey(string key)
            => string.Equals(key, MaxOptionKey, StringComparison.OrdinalIgnoreCase);

        private static TimeSpan GetWidestCandidateTime(Dictionary<string, TimeSpan> options, bool isStart, TimeSpan fallback)
        {
            var vals = GetOptionTimes(options);
            return vals.Count == 0 ? fallback : (isStart ? vals.Min() : vals.Max());
        }

        private static List<TimeSpan> GetOptionTimes(Dictionary<string, TimeSpan> options) =>
            options.Where(kvp => !IsMaxKey(kvp.Key)).Select(kvp => kvp.Value).ToList();

        // =====================================================================
        // The tail check: what did the chosen End append?
        // =====================================================================

        /// <summary>
        /// The AI chooses between dips, and sometimes the chosen End is a long way after the closest option before it (ex: after a
        /// pause, a laugh, a breath or a noise that follows the last word). When the audio that the chosen End appends to that
        /// option is at least EndTail.MinExtra long, it is worth asking what it is, without the text: it is only judged by what it
        /// sounds like.
        /// </summary>
        private class TailPass : TimingRefinePass
        {
            /// <summary>Where the End of a candidate stands in the tail check: the option it rests on, and how many times it was moved back.</summary>
            private class TailState
            {
                public TimeSpan Option;
                public IReadOnlyCollection<TimeSpan> OfferedOptions;
                public int Checks;

                /// <summary>Requests built, answered or not (runaway guard: an unanswered request is built again).</summary>
                public int Requests;
                public bool Done;
            }

            private readonly TimingRefineEndTailSettings r_settings;
            private readonly Dictionary<TimingCandidate, TailState> r_tailStates = new Dictionary<TimingCandidate, TailState>();
            private readonly IReadOnlyDictionary<string, float> r_keepIfSpeechRatio;

            public TailPass(TimingRefineEngineStartEndJudge engine, TimingRefiner refiner)
                : base(engine, refiner, "End tail", BoundarySide.End, engine.EndTail.InnerOptions, TimingMeta.TailKind, isTailCheck: true)
            {
                r_settings = engine.EndTail;
                r_keepIfSpeechRatio = new Dictionary<string, float>(
                    r_settings.KeepIfSpeechRatio ?? new Dictionary<string, float>(), StringComparer.OrdinalIgnoreCase);
            }

            /// <summary>Call after the End pass; CurrentRound is set on the candidates to ask (null on the others).</summary>
            public override void PrepareNextRound()
            {
                foreach (var cand in this.Refiner.Candidates)
                {
                    cand.CurrentRound = null;
                    if (!cand.End.Locked || cand.End.Fixed || cand.Origin == TimingOrigin.TitleCard)
                        continue;

                    if (!r_tailStates.TryGetValue(cand, out var state))
                    {
                        state = CreateTailState(cand);
                        r_tailStates[cand] = state;
                    }
                    if (state == null || state.Done)
                        continue;
                    if (state.Checks >= r_settings.MaxChecks || state.Requests >= r_settings.MaxChecks * 2)
                    {
                        state.Done = true;
                        continue;
                    }

                    var end = cand.End.Current;
                    var from = state.OfferedOptions.Where(t => t < state.Option - TimeSpan.FromMilliseconds(1)).DefaultIfEmpty(TimeSpan.MaxValue).Max();
                    if (from == TimeSpan.MaxValue || from <= cand.Start.Current + TimingRefiner.MinClipDuration || end - from < r_settings.MinExtra)
                    {
                        state.Done = true;
                        continue;
                    }

                    var round = new TimingRound
                    {
                        Side = BoundarySide.End,
                        RoundNumber = state.Checks + 1,
                        Center = end,
                        ClipAnchor = from,
                        TailFrom = from,
                        IsTailCheck = true,
                        TextSent = "" // the text is not sent: the audio is judged by what it sounds like
                    };
                    state.Requests++;
                    round.Options["Tail"] = end;
                    round.Clips.Add(new TimingClipOption { FieldName = "AudioClip", OptionId = "Tail", Start = from, End = end });
                    cand.CurrentRound = round;
                }
            }

            /// <summary>The state of a candidate's End at the start of its tail check (null when its End was not chosen by the AI between options).</summary>
            private static TailState CreateTailState(TimingCandidate cand)
            {
                var last = cand.Rounds.LastOrDefault(r => r.Side == BoundarySide.End && !r.IsTailCheck && r.Choice != null);
                if (last == null || !last.Options.TryGetValue(NormalizeToken(last.Choice), out var option))
                    return null;

                return new TailState { Option = option, OfferedOptions = last.Options.Values.ToList() };
            }

            public override void ProcessResults(IEnumerable<TimedItemWithMetadata> responseItems)
            {
                foreach (var item in responseItems)
                {
                    var cand = this.Refiner.FindCandidateFor(item.StartTime, c => c.CurrentRound?.IsTailCheck == true);
                    if (cand != null)
                        ApplyTailCheck(cand, item);
                }
            }

            private void ApplyTailCheck(TimingCandidate cand, TimedItemWithMetadata item)
            {
                var round = cand.CurrentRound;
                var state = r_tailStates[cand];
                round.Choice = GetTrimmedMeta(item, TimingMeta.TailKind);
                cand.TailChecks.Add(round);
                cand.CurrentRound = null;
                state.Checks++;

                var prevStart = cand.Start.Current;
                var prevEnd = cand.End.Current;
                var extra = $"{round.TailFrom:mm\\:ss\\.fff}-{round.Center:mm\\:ss\\.fff}";

                // No answer: the End is kept, as it was chosen
                bool noAnswer = string.IsNullOrWhiteSpace(round.Choice);
                if (noAnswer || string.Equals(round.Choice, TimingMeta.TailKindSpeech, StringComparison.OrdinalIgnoreCase))
                {
                    state.Done = true;
                    cand.Trace("Tail", prevStart, prevEnd, $"Extra {extra}: {(noAnswer ? "no answer" : round.Choice)}, End kept");
                    return;
                }

                // The terrain hears Speech over most of the extra (ex: "Silence" over loud audio, "Moaning" over the last words):
                // the answer is not trusted, the End is kept
                if (r_keepIfSpeechRatio.TryGetValue(round.Choice.Trim(), out var keepRatio) && keepRatio > 0f)
                {
                    var extraLength = round.Center - round.TailFrom;
                    var speech = this.Refiner.TerrainMapping.SpeechDurationBetween(round.TailFrom, round.Center);
                    if (extraLength > TimeSpan.Zero && speech.TotalMilliseconds >= extraLength.TotalMilliseconds * keepRatio)
                    {
                        state.Done = true;
                        cand.Trace("Tail", prevStart, prevEnd, $"Extra {extra}: {round.Choice} but terrain has {speech.TotalMilliseconds:0} ms of speech in {extraLength.TotalMilliseconds:0} ms, End kept");
                        return;
                    }
                }

                // Not speech: the End goes back to the closest option (and is checked again against the one before it)
                cand.End.Current = round.TailFrom;
                state.Option = round.TailFrom;
                MarkDipSelected(cand, BoundarySide.End, round.TailFrom);
                this.Refiner.TrimSilence(cand, BoundarySide.End);
                cand.Trace("Tail", prevStart, prevEnd, $"Extra {extra}: {round.Choice}, End moved back");
            }
        }
    }

    /// <summary>The settings of the End tail check of <see cref="TimingRefineEngineStartEndJudge"/>.</summary>
    public class TimingRefineEndTailSettings
    {
        [ConfigDoc("The options of the tail request: it must produce \"TailKind\". Defined in the main configuration.")]
        [JsonProperty(Order = 1)]
        public AIOptions InnerOptions { get; set; }

        [ConfigDoc("The extra audio appended by the chosen End must be at least this long to be checked (a shorter one is not worth a request).")]
        [JsonProperty(Order = 2)]
        public TimeSpan MinExtra { get; set; } = TimeSpan.FromMilliseconds(400);

        [ConfigDoc("How many times in a row the End of a subtitle can be moved back (the new End is checked against the option before it).",
            Advice = "1 is best: a second move back often cuts the end of the speech.")]
        [JsonProperty(Order = 3)]
        public int MaxChecks { get; set; } = 1;

        [ConfigDoc("By answer of the AI (TailKind): when the terrain finds Speech over at least this fraction of the extra audio (0 to 1), the answer is not trusted and the End is kept " +
            "(\"Silence\" over loud audio is wrong, \"Moaning\" over continuous speech is often the last words misidentified). A kind not listed always moves the End back.")]
        [JsonProperty(Order = 4, ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public Dictionary<string, float> KeepIfSpeechRatio { get; set; } = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            { "Silence", 0.3f },
            { "Moaning", 0.5f },
        };

        public void Validate()
        {
            if (this.InnerOptions == null)
                throw new InvalidOperationException("The End tail check ('EndTail') needs 'InnerOptions' (the options of its request).");
            if (this.KeepIfSpeechRatio != null && this.KeepIfSpeechRatio.Values.Any(v => v < 0f || v > 1f))
                throw new InvalidOperationException("'EndTail.KeepIfSpeechRatio' values must be between 0 and 1.");
            if (this.MinExtra < TimeSpan.Zero || this.MaxChecks < 1)
                throw new InvalidOperationException("'EndTail.MinExtra' can't be negative and 'EndTail.MaxChecks' must be at least 1.");
        }
    }
}
