using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>
    /// Refines the timing of transcription candidates in TWO sequential passes:
    ///
    ///   1. START pass: every clip of a node ends at the same fixed point (initial End + tail padding);
    ///      only the start differs (Audio_Max, Audio_S1 = tightest .. Audio_Sn = widest).
    ///   2. END pass: the Start is now locked, every clip begins at it;
    ///      only the end differs (Audio_Max, Audio_E1 = tightest .. Audio_En = widest).
    ///
    /// Each question the AI has to answer is therefore about ONE boundary.
    ///
    /// Neighbour handling (replaces the old reset/retry dance):
    ///   - Start pass: all Init values are suspect; a start may go back anywhere except onto/before the previous candidate's INITIAL start.
    ///   - Between the passes: every End is capped by the next (now locked) Start.
    /// </summary>
    public class TimingRefiner
    {
        // ---------------------------------------------------------------------
        // Constants
        // ---------------------------------------------------------------------

        private const float SpeechRatio = 4f;
        private const float PeakRatio = 6f;

        private static readonly TimeSpan MinClipDuration = TimeSpan.FromSeconds(0.05);
        private static readonly TimeSpan MinOverlapViableDuration = TimeSpan.FromSeconds(0.1);
        private static readonly TimeSpan MaxSnapDrift = TimeSpan.FromSeconds(1.2);
        private static readonly TimeSpan TinyOverlapThreshold = TimeSpan.FromMilliseconds(60);
        private static readonly TimeSpan MaxFinalClampOverlap = TimeSpan.FromMilliseconds(150);
        private const string MaxOptionKey = "Max";

        /// <summary>Max dips (S1..Sn / E1..En) offered to the AI in ONE request. More dips are spread evenly, then narrowed down around the AI's choice.</summary>
        private const int MaxTestsInRequest = 4;

        /// <summary>
        /// Runaway guard only. Every answer eliminates dips, so a boundary always converges on its own
        /// (a few answers in practice); this just keeps a bug from burning requests forever.
        /// </summary>
        private const int MaxAnswersPerBoundary = 10;

        // ---------------------------------------------------------------------
        // Settings & state
        // ---------------------------------------------------------------------

        public bool TrimSilenceOnStart { get; }
        public bool TrimSilenceOnEnd { get; }
        public TimeSpan MaxExpansion { get; }
        public TimeSpan BleedDetectionMargin { get; }
        public TimeSpan StartPassTailPadding { get; }

        public AudioTerrainMapping TerrainMapping { get; }
        public TimeSpan AudioEndTime { get; }

        /// <summary>Always kept sorted by OriginalStartTime.</summary>
        public List<TimingCandidate> Candidates { get; private set; } = new List<TimingCandidate>();

        private readonly Dictionary<TimeSpan, TimingCandidate> r_byOriginalStart = new Dictionary<TimeSpan, TimingCandidate>();
        private readonly TimingValidatorSettings r_validatorSettings;
        private readonly double? r_textMismatchMinSimilarity;

        /// <param name="validatorSettings">What the refiner flags while it works (null = nothing is flagged).</param>
        /// <param name="sharedTerrain">The terrain of a previous refiner of the same audio (it is long to compute), null to compute it.</param>
        public TimingRefiner(
            PcmAudio fullPcmAudioForRefiner,
            TimingRefinerSettings settings,
            TimingValidatorSettings validatorSettings,
            AudioTerrainMapping sharedTerrain = null)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            r_validatorSettings = validatorSettings;
            r_textMismatchMinSimilarity = validatorSettings?.Checks?.TextMismatchMinSimilarity;
            TrimSilenceOnStart = settings.TrimSilenceOnStart;
            TrimSilenceOnEnd = settings.TrimSilenceOnEnd;
            MaxExpansion = settings.MaxExpansion;
            BleedDetectionMargin = settings.BleedDetectionMargin;
            StartPassTailPadding = settings.StartPassTailPadding < TimeSpan.Zero ? TimeSpan.Zero : settings.StartPassTailPadding;
            AudioEndTime = fullPcmAudioForRefiner?.EndTime ?? TimeSpan.MaxValue;

            // Without refining there is no terrain: the refiner is then only the list of candidates
            if (!settings.Enabled)
            {
                TerrainMapping = null;
            }
            else if (sharedTerrain != null)
            {
                TerrainMapping = sharedTerrain;
            }
            else
            {
                TerrainMapping = new AudioTerrainMapping(fullPcmAudioForRefiner)
                {
                    SpeechRatio = SpeechRatio,
                    PeakRatio = PeakRatio,
                    ValleyReach = settings.ValleyReach,
                };
                TerrainMapping.Compute();
            }
        }

        // ---------------------------------------------------------------------
        // Small helpers
        // ---------------------------------------------------------------------

        /// <summary>Flags a candidate for review, unless the "Validations" setting says to ignore that kind of finding.</summary>
        public void Flag(TimingCandidate cand, ValidationCode code, string message)
        {
            if (ModeOf(code) == ValidationMode.Ignore)
                return;

            cand.AddValidationNote(new ValidationNote(code, message));
        }

        /// <summary>What to do for a code (see <see cref="TimingValidatorSettings.ModeOf"/>): Ignore when there are no validator settings.</summary>
        public ValidationMode ModeOf(ValidationCode code)
            => r_validatorSettings?.ModeOf(code) ?? ValidationMode.Ignore;

        private static TimeSpan Shift(TimeSpan t, BoundarySide side, MoveDirection dir, TimeSpan amount)
            => AudioTerrainMapping.MovesLater(side, dir) ? t + amount : t - amount;

        private static bool IsMaxKey(string key)
            => string.Equals(key, MaxOptionKey, StringComparison.OrdinalIgnoreCase);

        private static char SideChar(BoundarySide side) => side == BoundarySide.Start ? 'S' : 'E';

        private TimeSpan ClampToAudioBounds(TimeSpan t)
        {
            if (t < TimeSpan.Zero)
                return TimeSpan.Zero;
            if (t > AudioEndTime)
                return AudioEndTime;
            return t;
        }

        // ---------------------------------------------------------------------
        // Candidate creation (initial snapping only; dips are built when a phase begins)
        // ---------------------------------------------------------------------

        /// <summary>
        /// Adds a candidate as it is (no refining, no terrain): both boundaries are the ones of the item, and settled.
        /// The candidates must be added in order of start time.
        /// </summary>
        public void AddCandidateUnrefined(TimedItemWithMetadata originalItem, string originalText)
        {
            var candidate = new TimingCandidate
            {
                OriginalStartTime = originalItem.StartTime,
                OriginalEndTime = originalItem.EndTime,
                OriginalItem = originalItem,
                OriginalText = originalText,
            };
            candidate.Start.Init = candidate.Start.Current = originalItem.StartTime;
            candidate.End.Init = candidate.End.Current = originalItem.EndTime;
            candidate.LockAll();
            candidate.AddTraceRow("Orig", ' ', originalItem.StartTime, originalItem.EndTime, ' ', "", candidate.OriginalText);

            Candidates.Add(candidate);
            if (!r_byOriginalStart.ContainsKey(candidate.OriginalStartTime))
                r_byOriginalStart.Add(candidate.OriginalStartTime, candidate);
        }

        /// <summary>The candidates must be added in order of start time.</summary>
        /// <param name="fixedStart">The Start is already validated (outer edge of a split subtitle): it is used as is and never moves.</param>
        /// <param name="fixedEnd">Same for the End.</param>
        public void AddCandidate(
            TimedItemWithMetadata originalItem,
            string originalText,
            TimingOrigin origin = TimingOrigin.Original,
            TimeSpan? fixedStart = null,
            TimeSpan? fixedEnd = null)
        {
            var candidate = new TimingCandidate
            {
                OriginalStartTime = originalItem.StartTime,
                OriginalEndTime = originalItem.EndTime,
                OriginalItem = originalItem,
                OriginalText = originalText,
                Origin = origin,
            };

            TimeSpan start = SnapInitialBoundary(candidate, BoundarySide.Start);
            TimeSpan end = SnapInitialBoundary(candidate, BoundarySide.End);

            if (end <= start)
            {
                start = originalItem.StartTime;
                end = originalItem.EndTime;
            }

            EnforceMinimumDuration(candidate, ref start, ref end);

            if (fixedStart.HasValue)
            {
                start = fixedStart.Value;
                candidate.Start.Fixed = true;
            }
            if (fixedEnd.HasValue)
            {
                end = fixedEnd.Value;
                candidate.End.Fixed = true;
            }

            candidate.Start.Init = start;
            candidate.Start.Current = start;
            candidate.End.Init = end;
            candidate.End.Current = end;

            candidate.AddTraceRow("Orig", ' ', originalItem.StartTime, originalItem.EndTime, ' ', "", candidate.OriginalText);
            candidate.Trace("Init", originalItem.StartTime, originalItem.EndTime, "Snapped initial bounds");
            if (origin == TimingOrigin.SplitPart)
                candidate.Trace("Part", candidate.Start.Current, candidate.End.Current, "Part of a split subtitle (no text yet)");

            Candidates.Add(candidate);
            if (!r_byOriginalStart.ContainsKey(candidate.OriginalStartTime))
                r_byOriginalStart.Add(candidate.OriginalStartTime, candidate);

            // Keep sorted by construction (normally a no-op since the caller adds them in order)
            if (Candidates.Count > 1 && candidate.OriginalStartTime < Candidates[Candidates.Count - 2].OriginalStartTime)
                Candidates = Candidates.OrderBy(c => c.OriginalStartTime).ToList();
        }

        private static void EnsureDipPresent(List<BoundaryDipCandidate> list, TimeSpan time)
        {
            if (!list.Any(d => d.Time == time))
            {
                list.Add(new BoundaryDipCandidate(time));
                list.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
        }

        /// <summary>Finds a dip and accepts it only if it is within MaxSnapDrift of 't'.</summary>
        private TimeSpan? FindDipWithinDrift(TimeSpan t, BoundarySide side, MoveDirection dir, DipSearch search)
        {
            var dip = TerrainMapping.FindDip(t, side, dir, search);
            return dip.HasValue && (dip.Value - t).Duration() <= MaxSnapDrift ? dip : null;
        }

        public TimeSpan SnapInitialBoundary(TimingCandidate cand, BoundarySide side)
        {
            var t = side == BoundarySide.Start ? cand.OriginalStartTime : cand.OriginalEndTime;

            // 1. Silent: snap inward towards the speech edge (or the nearest dip)
            if (TerrainMapping.IsSilent(t))
            {
                var inwardLimit = Shift(t, side, MoveDirection.Inward, MaxSnapDrift);

                return FindDipWithinDrift(t, side, MoveDirection.Inward, new DipSearch
                       {
                           MinJump = TimeSpan.MaxValue,
                           StopAtSpeechEdge = true,
                           Inclusive = true,
                           Limit = inwardLimit
                       })
                    ?? FindDipWithinDrift(t, side, MoveDirection.Inward, new DipSearch
                       {
                           Inclusive = true,
                           Limit = inwardLimit
                       })
                    ?? t;
            }

            // 2. Inside a mountain: snap outward to the speech edge (or the nearest dip)
            return FindDipWithinDrift(t, side, MoveDirection.Outward, new DipSearch
                   {
                       StopAtSpeechEdge = true,
                       Inclusive = true,
                       Limit = Shift(t, side, MoveDirection.Outward, MaxSnapDrift)
                   })
                ?? FindDipWithinDrift(t, side, MoveDirection.Outward, new DipSearch { Inclusive = true })
                ?? t;
        }

        /// <summary>Only keeps the starts ordered and the end after the start. There is NO minimum subtitle duration.</summary>
        public void EnforceMinimumDuration(TimingCandidate cand, ref TimeSpan start, ref TimeSpan end)
        {
            var prev = Candidates.LastOrDefault();
            if (prev != null && start <= prev.Start.Current)
            {
                var nextDip = TerrainMapping.FindDip(
                    prev.Start.Current,
                    BoundarySide.Start,
                    MoveDirection.Inward,
                    new DipSearch
                    {
                        Inclusive = false,
                        Limit = cand.OriginalStartTime + MaxSnapDrift
                    });

                if (nextDip.HasValue && (nextDip.Value - cand.OriginalStartTime).Duration() <= MaxSnapDrift)
                {
                    start = nextDip.Value;
                }
                else if (cand.OriginalStartTime > prev.Start.Current)
                {
                    start = cand.OriginalStartTime;
                }
            }

            if (end <= start)
            {
                end = cand.OriginalEndTime > start ? cand.OriginalEndTime : start + MinClipDuration;
            }
        }

        // ---------------------------------------------------------------------
        // Phases
        // ---------------------------------------------------------------------

        /// <summary>
        /// Call once before the first round of a side.
        /// Start: builds the Start dips and forbids starts that would go back into the previous candidate's initial end.
        /// End:   builds the End dips (the Starts are locked now) and caps every End by the next Start.
        /// </summary>
        public void BeginPhase(BoundarySide side)
        {
            for (int i = 0; i < Candidates.Count; i++)
                InitializeDips(i, side);

            if (side == BoundarySide.Start)
                ApplyStartFloors();
            else
                CapEndsByNextStarts();
        }

        /// <summary>Call after the last round of a side: whatever is still unlocked is locked and flagged.</summary>
        public void EndPhase(BoundarySide side)
        {
            foreach (var cand in Candidates)
            {
                cand.CurrentRound = null;

                var boundary = cand[side];
                if (boundary.Locked)
                    continue;

                boundary.Locked = true;
                Flag(cand, ValidationCode.Unfinished, $"Unfinished {side} (safety limit reached)");
                cand.Trace("Fin", cand.Start.Current, cand.End.Current, $"{side} left unfinished");
            }
        }

        private void InitializeDips(int index, BoundarySide side)
        {
            var cand = Candidates[index];
            var boundary = cand[side];
            var (_, durationAnchor) = AnchorsFor(index, side);

            var max = ComputeMax(cand, side, boundary.Current, durationAnchor);
            var inwardLimit = InwardLimit(side, durationAnchor);

            boundary.Dips.Clear();
            foreach (var t in TerrainMapping.GetDips(side, inwardLimit, max))
                boundary.Dips.Add(new BoundaryDipCandidate(t));

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
                        Flag(cur, ValidationCode.Overlap, "Overlaps with adjacent subtitle");
                        Flag(next, ValidationCode.Overlap, "Overlaps with adjacent subtitle");
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

            var dip = TerrainMapping.FindDip(cap, BoundarySide.End, MoveDirection.Inward, new DipSearch
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
        // Round preparation
        // ---------------------------------------------------------------------

        /// <summary>
        /// Prepares the next round for the given side on every candidate whose boundary is still open.
        /// CurrentRound stays null for locked / exhausted candidates.
        /// In the End phase, a boundary with a single possible option is locked without asking the AI.
        /// (The Start phase always asks: it is also the pass that verifies the text and detects inaudible nodes.)
        /// </summary>
        public void PrepareNextRound(BoundarySide side)
        {
            for (int i = 0; i < Candidates.Count; i++)
            {
                var cand = Candidates[i];
                var boundary = cand[side];

                cand.CurrentRound = null;
                if (boundary.Locked || boundary.RetryCount >= MaxAnswersPerBoundary)
                    continue;

                if (side == BoundarySide.Start)
                {
                    cand.PossibleBleedFromPrev = i > 0
                        ? BleedTextFrom(Candidates[i - 1], cand.Start.Current - Candidates[i - 1].End.Current)
                        : null;
                    cand.PossibleBleedFromNext = null;
                }
                else
                {
                    cand.PossibleBleedFromPrev = null;
                    cand.PossibleBleedFromNext = i < Candidates.Count - 1
                        ? BleedTextFrom(Candidates[i + 1], Candidates[i + 1].Start.Current - cand.End.Current)
                        : null;
                }

                var round = BuildRoundForSide(i, side);

                if ((side == BoundarySide.End && round.IsTrivial) || round.IsIncumbentOnly)
                {
                    LockWithoutAsking(cand, round);
                    continue;
                }

                cand.CurrentRound = round;
            }
        }

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

            TrimSilence(cand, round.Side);
        }

        private string BleedTextFrom(TimingCandidate neighbour, TimeSpan gap)
            => gap <= BleedDetectionMargin && !string.IsNullOrWhiteSpace(neighbour.TextForAI)
                ? neighbour.TextForAI
                : null;

        /// <summary>
        /// clipAnchor     = the fixed end of every clip.
        /// durationAnchor = the other boundary, used to keep the dips from squashing the subtitle.
        /// Start side: clips run [option .. End + tail padding]. End side: clips run [locked Start .. option].
        /// </summary>
        private (TimeSpan clipAnchor, TimeSpan durationAnchor) AnchorsFor(int index, BoundarySide side)
        {
            var cand = Candidates[index];
            if (side == BoundarySide.End)
                return (cand.Start.Current, cand.Start.Current);

            var end = cand.End.Current;
            var tail = end + StartPassTailPadding;

            // Never let the tail run into the first sound of the next candidate
            if (index + 1 < Candidates.Count)
            {
                var nextStart = Candidates[index + 1].Start.Init;
                if (nextStart > end && nextStart < tail)
                    tail = nextStart;
            }

            var clipEnd = ClampToAudioBounds(tail);
            if (clipEnd < end)
                clipEnd = end;

            return (clipEnd, end);
        }

        private static TimeSpan InwardLimit(BoundarySide side, TimeSpan durationAnchor)
            => side == BoundarySide.Start ? durationAnchor - MinClipDuration : durationAnchor + MinClipDuration;

        private TimingRound BuildRoundForSide(int index, BoundarySide side)
        {
            var cand = Candidates[index];
            var boundary = cand[side];
            var center = boundary.Current;
            var (clipAnchor, durationAnchor) = AnchorsFor(index, side);
            var inwardLimit = InwardLimit(side, durationAnchor);

            var round = new TimingRound
            {
                Side = side,
                RoundNumber = boundary.RetryCount + 1,
                Center = center,
                ClipAnchor = clipAnchor,
                // The Start pass judges the raw draft; the End pass benefits from what the Start pass heard
                TextSent = side == BoundarySide.Start ? cand.OriginalText : cand.TextForAI
            };

            var max = ComputeMax(cand, side, center, durationAnchor);
            var active = SelectActiveDips(cand, side, center, inwardLimit, max);

            // An already validated boundary has nothing to choose from (the Start pass still asks, for the text)
            if (boundary.Fixed)
            {
                max = center;
                active = new List<TimeSpan> { center };
            }

            // Neutral labels, ordered from tightest (innermost) to widest (outermost)
            var ordered = LabelOptions(side, active, round);

            // Clamp Max to the widest option if the terrain beyond it is only silence
            max = TrimSilentMargin(side, max, ordered[ordered.Count - 1]);
            round.Options[MaxOptionKey] = max;

            AddClips(round, side, ordered, max, clipAnchor);

            // For the trace: what was still in play at the start of this round (hard limits included), plus what was offered (Max included)
            foreach (var d in boundary.Dips.Where(d => d.State != DipCandidateState.Eliminated))
                round.DipTimesAtStart.Add(d.Time);
            foreach (var t in round.Options.Values)
                round.DipTimesAtStart.Add(t);

            // All the options are sent as they are, but a request is useless when they all give the same final result
            // (after the silence trimming): nothing to choose from.
            var finalOfTightest = TrimmedTime(cand, side, ordered[0]);
            bool allSameFinal = ordered.All(t => TrimmedTime(cand, side, t) == finalOfTightest);

            round.IsTrivial = allSameFinal
                && TrimmedTime(cand, side, max) == finalOfTightest;

            // A refinement round (the previous answer is the incumbent) with nothing left that could change the result
            round.IsIncumbentOnly = allSameFinal
                && boundary.Dips.Any(d => d.Time == center && d.State == DipCandidateState.Selected);

            return round;
        }

        private List<TimeSpan> SelectActiveDips(
            TimingCandidate cand,
            BoundarySide side,
            TimeSpan center,
            TimeSpan inwardDurationLimit,
            TimeSpan outwardMax)
        {
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
            var slots = Math.Max(1, MaxTestsInRequest - incumbents.Count);

            var selected = incumbents
                .Concat(SampleEvenly(others, slots))
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            return selected.Count > 0 ? selected : new List<TimeSpan> { center };
        }

        /// <summary>
        /// Picks 'count' items spread evenly over the sorted list (all of them when the list is not longer than 'count').
        /// The list is cut in 'count' * 2 sections and the boundaries 1, 3, 5, ... are used (the first and last sections are half size).
        /// Ex: 15 items, count = 4 => the 2nd, 6th, 10th and 14th.
        /// </summary>
        private static List<TimeSpan> SampleEvenly(List<TimeSpan> sorted, int count)
        {
            if (sorted.Count <= count)
                return sorted;

            var result = new List<TimeSpan>();
            for (int j = 1; j <= count; j++)
            {
                var index = (int)Math.Floor((2 * j - 1) * sorted.Count / (2.0 * count));
                result.Add(sorted[Math.Min(Math.Max(index, 0), sorted.Count - 1)]);
            }
            return result.Distinct().ToList();
        }

        /// <summary>Furthest the boundary may go outward (limited by MaxExpansion, audio bounds and hard limits).</summary>
        private TimeSpan ComputeMax(TimingCandidate cand, BoundarySide side, TimeSpan center, TimeSpan durationAnchor)
        {
            var hardLimit = GetHardLimit(cand, side);

            if (side == BoundarySide.Start)
            {
                var max = ClampToAudioBounds(center - MaxExpansion);
                return hardLimit.HasValue && max < hardLimit.Value ? hardLimit.Value : max;
            }

            var endMax = ClampToAudioBounds(center + MaxExpansion);
            if (hardLimit.HasValue && endMax > hardLimit.Value)
                endMax = hardLimit.Value;
            if (endMax <= durationAnchor)
                endMax = ClampToAudioBounds(durationAnchor + MinClipDuration);
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

        private static string OptionPrefix(BoundarySide side) => side == BoundarySide.Start ? "S" : "E";
        private static string OptionKey(BoundarySide side, int index) => $"{OptionPrefix(side)}{index}";

        private TimeSpan TrimSilentMargin(BoundarySide side, TimeSpan max, TimeSpan widest)
        {
            bool beyondWidest = side == BoundarySide.Start ? max < widest : max > widest;
            if (!beyondWidest)
                return max;

            var (from, to) = side == BoundarySide.Start ? (max, widest) : (widest, max);
            return TerrainMapping.HasSpeechBetween(from, to) ? max : widest;
        }

        /// <summary>Audio_Max first (the wide reference), then Audio_S1/E1 (tightest) to the widest.</summary>
        private void AddClips(
            TimingRound round,
            BoundarySide side,
            List<TimeSpan> ordered,
            TimeSpan max,
            TimeSpan clipAnchor)
        {
            AddClip(round, side, MaxOptionKey, max, clipAnchor);
            for (int i = 0; i < ordered.Count; i++)
                AddClip(round, side, OptionKey(side, i + 1), ordered[i], clipAnchor);
        }

        private void AddClip(TimingRound round, BoundarySide side, string optionId, TimeSpan boundaryTime, TimeSpan clipAnchor)
        {
            TimeSpan start = side == BoundarySide.Start ? boundaryTime : clipAnchor;
            TimeSpan end = side == BoundarySide.Start ? clipAnchor : boundaryTime;

            // Only possible for the initial boundary of a very short subtitle: never send an empty clip
            if (end - start < MinClipDuration)
            {
                if (side == BoundarySide.End)
                    end = ClampToAudioBounds(start + MinClipDuration);
                else
                    start = ClampToAudioBounds(end - MinClipDuration);
            }

            round.Clips.Add(new TimingClipOption
            {
                FieldName = $"Audio_{optionId}",
                OptionId = optionId,
                Start = start,
                End = end
            });
        }

        /// <summary>The dips that were in play when the round was built, with their state after the AI's answer.</summary>
        private static string FormatDips(TimingCandidate cand, BoundarySide side, TimingRound round)
        {
            var initTime = cand[side].Init;
            return string.Join(" ", cand[side].Dips
                .Where(d => round.DipTimesAtStart.Contains(d.Time))
                .Select(d => d.Format(initTime)));
        }

        // ---------------------------------------------------------------------
        // AI response processing
        // ---------------------------------------------------------------------

        public void ProcessBatchResults(IEnumerable<TranscribedItem> responseItems, BoundarySide side)
        {
            foreach (var item in responseItems)
            {
                var cand = FindCandidateFor(item, side);
                if (cand != null)
                    ApplyResponse(cand, item, side);
            }
        }

        private static bool IsAwaiting(TimingCandidate cand, BoundarySide side)
            => cand.CurrentRound != null && cand.CurrentRound.Side == side;

        private TimingCandidate FindCandidateFor(TranscribedItem item, BoundarySide side)
        {
            if (r_byOriginalStart.TryGetValue(item.StartTime, out var exact) && IsAwaiting(exact, side))
                return exact;

            return Candidates.FirstOrDefault(c =>
                IsAwaiting(c, side) &&
                Math.Abs((c.OriginalStartTime - item.StartTime).TotalMilliseconds) < 50);
        }

        private static string GetTrimmedMeta(TranscribedItem item, string key)
        {
            item.Metadata.TryGetValue(key, out var value);
            return value?.Trim();
        }

        private void ApplyResponse(TimingCandidate cand, TranscribedItem item, BoundarySide side)
        {
            var round = cand.CurrentRound;
            var boundary = cand[side];
            boundary.RetryCount++;

            round.Choice = GetTrimmedMeta(item, side == BoundarySide.Start ? TimingMeta.BestStart : TimingMeta.BestEnd);
            round.HeardText = GetTrimmedMeta(item, TimingMeta.HeardText);
            cand.Rounds.Add(round);
            cand.CurrentRound = null;


            var step = $"#{boundary.RetryCount}{SideChar(side)}";
            var prevStart = cand.Start.Current;
            var prevEnd = cand.End.Current;

            if (IsInaudibleToken(round.Choice))
            {
                cand.LockAll();
                Flag(cand, ValidationCode.Inaudible, $"Audio Inaudible (original): {cand.OriginalText}");
                cand.Trace(step, prevStart, prevEnd, "AI: Inaudible", round.HeardText);
                return;
            }

            ApplySideChoice(cand, round, side);

            // Guard against crossed boundaries
            bool crossed = side == BoundarySide.Start
                ? cand.Start.Current >= cand.End.Current
                : cand.End.Current <= cand.Start.Current;
            if (crossed)
            {
                // (no silence trimming: the previous timing is kept as is)
                boundary.Current = round.Center;
                boundary.Locked = true;
                Flag(cand, ValidationCode.BoundariesCrossed, $"Boundaries crossed ({side}), kept previous timing");
            }

            // HeardText: what the AI actually heard when it disagrees with the text it was given
            bool hasHeard = !string.IsNullOrWhiteSpace(round.HeardText)
                && !string.Equals(round.HeardText, "null", StringComparison.OrdinalIgnoreCase);
            if (hasHeard && !IsSameText(round.TextSent, round.HeardText))
            {
                // The End pass is judged on what was heard in the Start pass
                if (side == BoundarySide.Start)
                    cand.HeardText = round.HeardText;

                if (side == BoundarySide.Start && cand.Origin != TimingOrigin.Original)
                {
                    // A split part has no text of its own: what the Start pass heard IS its text (not a mismatch)
                    cand.Trace("Text", cand.Start.Current, cand.End.Current, "Text given by what was heard", round.HeardText);
                }
                else
                {
                    Flag(cand, ValidationCode.TextMismatch, side == BoundarySide.Start
                        ? $"Text Mismatch (original vs heard in Max): `{cand.OriginalText}` vs `{round.HeardText}`"
                        : $"Text Mismatch (start-pass text vs heard in End pass): `{round.TextSent}` vs `{round.HeardText}`");
                }
            }

            cand.Traces.Add($"{step} {side} options:");
            cand.Traces.Add($"     {FormatDips(cand, side, round)}");
            cand.Trace(step, prevStart, prevEnd, $"AI: {round.Choice}", round.HeardText);

            if (!crossed && boundary.Locked)
                TrimSilence(cand, side);
        }

        /// <summary>The same text, or (when TextMismatchMinSimilarity is set) similar enough not to be a mismatch.</summary>
        private bool IsSameText(string sent, string heard)
        {
            if (string.Equals(sent, heard, StringComparison.OrdinalIgnoreCase))
                return true;

            return r_textMismatchMinSimilarity.HasValue
                && TextSimilarity.Ratio(sent, heard) >= r_textMismatchMinSimilarity.Value;
        }

        /// <summary>
        /// Where a boundary placed at 't' really ends up once the silence is trimmed (== t when trimming is off for the side,
        /// when 't' is inside speech, or when trimming would squash the subtitle).
        /// Two options with the same TrimmedTime give the same final result, so only one of them is worth testing.
        /// </summary>
        private TimeSpan TrimmedTime(TimingCandidate cand, BoundarySide side, TimeSpan t)
        {
            if (!(side == BoundarySide.Start ? TrimSilenceOnStart : TrimSilenceOnEnd))
                return t;

            var trimmed = TerrainMapping.TrimSilence(t, side);
            bool valid = side == BoundarySide.Start
                ? trimmed < cand.End.Current - MinClipDuration
                : trimmed > cand.Start.Current + MinClipDuration;
            return valid ? trimmed : t;
        }

        /// <summary>The AI may keep silence in its final choice: according to the terrain, move the boundary inward over it.</summary>
        private void TrimSilence(TimingCandidate cand, BoundarySide side)
        {
            var boundary = cand[side];
            var trimmed = TrimmedTime(cand, side, boundary.Current);
            if (trimmed == boundary.Current)
                return;

            var prevStart = cand.Start.Current;
            var prevEnd = cand.End.Current;
            var amount = (trimmed - boundary.Current).Duration();
            boundary.Current = trimmed;
            cand.Trace("Trim", prevStart, prevEnd, $"{side} silence trimmed ({amount.TotalSeconds:0.000}s)");
        }

        private void ApplySideChoice(TimingCandidate cand, TimingRound round, BoundarySide side)
        {
            var boundary = cand[side];

            var (time, locked, needsMore) = ResolveBoundaryChoice(cand, round, side, round.Choice);
            if (boundary.Fixed)
                (time, locked, needsMore) = (round.Center, true, false);
            boundary.Current = time;
            boundary.Locked = locked;

            if (needsMore && locked)
            {
                boundary.Locked = true;
                Flag(cand, side == BoundarySide.Start ? ValidationCode.MissingStart : ValidationCode.MissingEnd, $"Missing {side} (Max expansion reached)");
            }
        }

        private static bool IsInaudibleToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;
            return token.IndexOf("Inaudible", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---------------------------------------------------------------------
        // Boundary choice resolution
        // ---------------------------------------------------------------------

        private (TimeSpan resolvedTime, bool isLocked, bool needsMoreExpansion) ResolveBoundaryChoice(
            TimingCandidate cand,
            TimingRound round,
            BoundarySide side,
            string rawChoice)
        {
            var options = round.Options;
            var fallback = round.Center;

            if (string.IsNullOrWhiteSpace(rawChoice))
            {
                Flag(cand, ValidationCode.NoAnswer, $"No {side} answer from the AI, kept previous timing");
                return (fallback, true, false);
            }

            string token = NormalizeToken(rawChoice);

            if (string.Equals(token, MaxOptionKey, StringComparison.OrdinalIgnoreCase))
            {
                return ResolveMaxChoice(cand, side, options, fallback);
            }

            if (options.TryGetValue(token, out var resolved))
            {
                var hasMore = MarkDipsAfterChoice(cand, side, options, resolved, narrowToWindow: true);
                return (resolved, !hasMore, false);
            }

            Flag(cand, ValidationCode.UnrecognisedAnswer, $"Unrecognised {side} answer '{rawChoice}', kept previous timing");
            MarkDipsAfterChoice(cand, side, options, fallback, narrowToWindow: false);
            return (fallback, true, false);
        }

        /// <summary>
        /// Accepts "S2", "Max", "Audio_S2" (the clip field name) and "Audio_Max". Strips a trailing '*'.
        /// </summary>
        private static string NormalizeToken(string rawChoice)
        {
            string token = (rawChoice ?? "").Trim();

            const string fieldPrefix = "Audio_";
            if (token.StartsWith(fieldPrefix, StringComparison.OrdinalIgnoreCase))
                token = token.Substring(fieldPrefix.Length);

            return token.TrimEnd('*').Trim();
        }

        private static void MarkDipSelected(TimingCandidate cand, BoundarySide side, TimeSpan resolved)
        {
            foreach (var d in cand[side].Dips)
            {
                if (d.Time == resolved && d.State != DipCandidateState.HardLimit)
                    d.State = DipCandidateState.Selected;
            }
        }

        /// <summary>
        /// The chosen dip becomes Selected and the other tested dips are eliminated.
        /// With narrowToWindow (binary search), the untested dips beyond the tested neighbours of the choice
        /// are eliminated too: only the ones between those neighbours are still worth testing.
        /// </summary>
        /// <returns>true when untested dips remain (another request is needed to narrow the choice).</returns>
        private bool MarkDipsAfterChoice(
            TimingCandidate cand,
            BoundarySide side,
            Dictionary<string, TimeSpan> options,
            TimeSpan resolved,
            bool narrowToWindow)
        {
            var testedTimes = new HashSet<TimeSpan>(options.Values);

            // Nearest tested neighbours of the choice (null = the choice is the last tested on that side: the window stays open)
            var optionTimes = GetOptionTimes(options);
            TimeSpan? lower = optionTimes.Where(t => t < resolved).Select(t => (TimeSpan?)t).Max();
            TimeSpan? upper = optionTimes.Where(t => t > resolved).Select(t => (TimeSpan?)t).Min();

            // An untested dip that gives the same final result as the choice (after the silence trimming) is not worth a request
            var resolvedFinal = TrimmedTime(cand, side, resolved);

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
                else if (narrowToWindow && d.State == DipCandidateState.Untested && TrimmedTime(cand, side, d.Time) != resolvedFinal)
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
            BoundarySide side,
            Dictionary<string, TimeSpan> options,
            TimeSpan fallback)
        {
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

            return (widestC, false, true);
        }

        /// <summary>Eliminates every dip at or inside 'boundaryTime' (later for Start, earlier for End).</summary>
        private static void EliminateDips(TimingCandidate cand, BoundarySide side, TimeSpan boundaryTime)
        {
            bool isStart = side == BoundarySide.Start;
            foreach (var d in cand[side].Dips)
            {
                if (d.State == DipCandidateState.HardLimit)
                    continue;

                bool shouldEliminate = isStart ? d.Time >= boundaryTime : d.Time <= boundaryTime;
                if (shouldEliminate && d.State != DipCandidateState.Selected)
                    d.State = DipCandidateState.Eliminated;
            }
        }

        private static TimeSpan GetWidestCandidateTime(Dictionary<string, TimeSpan> options, bool isStart, TimeSpan fallback)
        {
            var vals = GetOptionTimes(options);
            return vals.Count == 0 ? fallback : (isStart ? vals.Min() : vals.Max());
        }

        private static List<TimeSpan> GetOptionTimes(Dictionary<string, TimeSpan> options) =>
            options.Where(kvp => !IsMaxKey(kvp.Key)).Select(kvp => kvp.Value).ToList();

        // ---------------------------------------------------------------------
        // Hard limits
        // ---------------------------------------------------------------------

        private static TimeSpan? GetHardLimit(TimingCandidate cand, BoundarySide side)
        {
            var hardDips = cand[side].Dips.Where(d => d.State == DipCandidateState.HardLimit).ToList();
            if (hardDips.Count == 0) return null;

            bool isStart = side == BoundarySide.Start;
            if (isStart)
            {
                var firstForbidden = hardDips.Max(d => d.Time);
                var allowed = cand[side].Dips.Where(d => d.State != DipCandidateState.HardLimit && d.Time >= firstForbidden).ToList();
                return allowed.Count > 0 ? allowed.Min(d => d.Time) : firstForbidden;
            }
            else
            {
                var firstForbidden = hardDips.Min(d => d.Time);
                var allowed = cand[side].Dips.Where(d => d.State != DipCandidateState.HardLimit && d.Time <= firstForbidden).ToList();
                return allowed.Count > 0 ? allowed.Max(d => d.Time) : firstForbidden;
            }
        }

        /// <summary>Forbids every dip beyond 'limit' (earlier for Start, later for End).</summary>
        private static void SetHardLimit(TimingCandidate cand, BoundarySide side, TimeSpan limit, bool ensurePresent)
        {
            if (ensurePresent)
                EnsureDipPresent(cand[side].Dips, limit);

            bool isStart = side == BoundarySide.Start;
            foreach (var d in cand[side].Dips)
            {
                if (isStart ? d.Time < limit : d.Time > limit)
                    d.State = DipCandidateState.HardLimit;
            }
        }

        // ---------------------------------------------------------------------
        // Finalization & stats
        // ---------------------------------------------------------------------

        /// <summary>Puts the (already refined) candidates in the place of the ones they replace (the parts of a split subtitle).</summary>
        public void ReplaceCandidates(IEnumerable<TimingCandidate> replaced, IEnumerable<TimingCandidate> replacements)
        {
            foreach (var cand in replaced.ToList())
            {
                Candidates.Remove(cand);
                if (r_byOriginalStart.TryGetValue(cand.OriginalStartTime, out var registered) && registered == cand)
                    r_byOriginalStart.Remove(cand.OriginalStartTime);
            }

            foreach (var cand in replacements)
            {
                Candidates.Add(cand);
                if (!r_byOriginalStart.ContainsKey(cand.OriginalStartTime))
                    r_byOriginalStart.Add(cand.OriginalStartTime, cand);
            }

            Candidates = Candidates.OrderBy(c => c.OriginalStartTime).ToList();
        }

        /// <summary>
        /// Adds the title cards (times of the whole timeline) between the other candidates. They are never refined or checked:
        /// they have no text, their on-screen text is read later (see {GrabOnScreenText} in the output).
        /// </summary>
        public void SpliceTitleCards(IEnumerable<(TimeSpan Start, TimeSpan End)> cards)
        {
            var added = new List<TimingCandidate>();
            foreach (var (start, end) in cards)
            {
                var cand = new TimingCandidate
                {
                    OriginalStartTime = start,
                    OriginalEndTime = end,
                    OriginalItem = new TimedItemWithMetadata(start, end),
                    OriginalText = "",
                    Origin = TimingOrigin.TitleCard,
                };
                foreach (var boundary in new[] { cand.Start, cand.End })
                    boundary.Locked = true;
                cand.Start.Init = cand.Start.Current = start;
                cand.End.Init = cand.End.Current = end;

                cand.AddTraceRow("Card", ' ', start, end, ' ', "Title card detected in the video");
                Flag(cand, ValidationCode.TitleCard, "Title card detected (on-screen text to read)");
                added.Add(cand);
            }

            ReplaceCandidates(Enumerable.Empty<TimingCandidate>(), added);
        }

        /// <summary>Last safety net: clamps slight overlaps at the midpoint, flags the others for review.</summary>
        public void FinalizeOverlaps()
        {
            for (int i = 0; i < Candidates.Count - 1; i++)
            {
                var cur = Candidates[i];
                var next = Candidates[i + 1];

                if (cur.End.Current <= next.Start.Current)
                    continue;

                var overlap = cur.End.Current - next.Start.Current;
                if (overlap <= MaxFinalClampOverlap)
                {
                    var mid = TimeSpan.FromMilliseconds((cur.End.Current.TotalMilliseconds + next.Start.Current.TotalMilliseconds) / 2.0);
                    if (mid - cur.Start.Current >= MinClipDuration && next.End.Current - mid >= MinClipDuration)
                    {
                        var curPrevEnd = cur.End.Current;
                        var nextPrevStart = next.Start.Current;
                        cur.End.Current = mid;
                        next.Start.Current = mid;
                        cur.Trace("Clmp", cur.Start.Current, curPrevEnd, "Clamped minor overlap with next");
                        next.Trace("Clmp", nextPrevStart, next.End.Current, "Clamped minor overlap with prev");
                        continue;
                    }
                }

                Flag(cur, ValidationCode.Overlap, "Overlaps with adjacent subtitle");
                Flag(next, ValidationCode.Overlap, "Overlaps with adjacent subtitle");
            }
        }

        /// <summary>Computes the summary numbers (null when there are no candidates).</summary>
        public TimingRefinerStats ComputeStats()
        {
            // The title cards are not refined: they are left out of the numbers
            var subs = Candidates.Where(c => c.Origin != TimingOrigin.TitleCard).ToList();
            if (subs.Count == 0)
                return null;

            var stats = new TimingRefinerStats
            {
                Total = subs.Count,
                TitleCards = Candidates.Count - subs.Count,
                Review = subs.Count(c => c.NeedsReview),
            };
            stats.Clean = stats.Total - stats.Review;

            foreach (var (code, _) in TimingRefinerStats.NoteCategories)
                stats.NoteCounts[code] = Candidates.Count(c => c.ValidationNotes.Any(n => n.Code == code));

            stats.AvgOriginalDuration = subs.Average(c => (c.OriginalEndTime - c.OriginalStartTime).TotalSeconds);
            stats.AvgFinalDuration = subs.Average(c => (c.End.Current - c.Start.Current).TotalSeconds);
            stats.AvgStartDrift = subs.Average(c => (c.Start.Current - c.OriginalStartTime).TotalSeconds);
            stats.MaxOutwardStartDrift = subs.Min(c => (c.Start.Current - c.OriginalStartTime).TotalSeconds);
            stats.AvgEndDrift = subs.Average(c => (c.End.Current - c.OriginalEndTime).TotalSeconds);
            stats.MaxOutwardEndDrift = subs.Max(c => (c.End.Current - c.OriginalEndTime).TotalSeconds);

            for (int i = 0; i < subs.Count; i++)
            {
                if (subs[i].End.Current - subs[i].Start.Current <= MinClipDuration)
                    stats.Squashed++;
                if (i < subs.Count - 1 && subs[i].End.Current > subs[i + 1].Start.Current)
                    stats.Overlaps++;
            }

            return stats;
        }
    }
}
