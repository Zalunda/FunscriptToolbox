using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Inputs;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Processors.Refiner
{
    /// <summary>
    /// The refining of the timings of the subtitles (candidates): what is common to all the engines (see <see cref="TimingRefineEngine"/>).
    /// - The terrain of the audio (where the speech is), and the initial snap of each boundary on it (<see cref="AddCandidate"/>).
    /// - The candidates and their state (their boundaries, their rounds, their flags and traces).
    /// - The silence trimming of a boundary, the overlaps at the end, the numbers of the summary.
    /// The questions asked to the AI, and what is done with the answers, are the engine's (its passes, created for each refiner).
    /// </summary>
    public class TimingRefiner
    {
        // ---------------------------------------------------------------------
        // Constants
        // ---------------------------------------------------------------------

        /// <summary>The shortest clip / subtitle (a boundary never goes closer than this to the other one).</summary>
        internal static readonly TimeSpan MinClipDuration = TimeSpan.FromSeconds(0.05);
        private static readonly TimeSpan MaxSnapDrift = TimeSpan.FromSeconds(1.2);
        internal static readonly TimeSpan MaxFinalClampOverlap = TimeSpan.FromMilliseconds(150);

        // ---------------------------------------------------------------------
        // Settings & state
        // ---------------------------------------------------------------------

        public bool TrimSilenceOnStart { get; }
        public bool TrimSilenceOnEnd { get; }
        public TimeSpan BleedDetectionMargin { get; }

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
            BleedDetectionMargin = settings.BleedDetectionMargin;
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
                    ValleyReach = settings.ValleyReach,
                };
                TerrainMapping.Compute();
            }
        }

        // ---------------------------------------------------------------------
        // Small helpers
        // ---------------------------------------------------------------------

        /// <summary>Flags a candidate for review, unless the "Validations" setting says to ignore that kind of finding.</summary>
        /// <param name="detail">A sub-kind of the code (ex. the name of the MaybePointless pattern), or null.</param>
        public void Flag(TimingCandidate cand, ValidationCode code, string message, string detail = null)
        {
            if (ModeOf(code) == ValidationMode.Ignore)
                return;

            cand.AddValidationNote(new ValidationNote(code, message, detail));
        }

        /// <summary>What to do for a code (see <see cref="TimingValidatorSettings.ModeOf"/>): Ignore when there are no validator settings.</summary>
        public ValidationMode ModeOf(ValidationCode code)
            => r_validatorSettings?.ModeOf(code) ?? ValidationMode.Ignore;

        private static TimeSpan Shift(TimeSpan t, BoundarySide side, MoveDirection dir, TimeSpan amount)
            => AudioTerrainMapping.MovesLater(side, dir) ? t + amount : t - amount;

        internal TimeSpan ClampToAudioBounds(TimeSpan t)
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
        // The passes of the engine
        // ---------------------------------------------------------------------

        /// <summary>
        /// After the passes of the engine: the boundaries of the sides that were not asked stay where the initial snap put them
        /// (they are locked). No pass at all (ex. TimingRefineEngineNone): the timings are kept as snapped on the audio.
        /// </summary>
        public void LockUnaskedBoundaries(IReadOnlyList<TimingRefinePass> passes)
        {
            var asked = passes.Where(p => !p.IsTailCheck).Select(p => p.Side).Distinct().ToList();
            foreach (var cand in Candidates)
            {
                foreach (var side in TimingCandidate.Sides.Where(s => !asked.Contains(s)))
                    cand[side].Locked = true;

                if (asked.Count == 0)
                    cand.Trace("Snap", cand.Start.Current, cand.End.Current, "Kept as snapped on the audio");
            }
        }

        /// <summary>
        /// The candidate an answer is for (the answers are at the original start time of their candidate): the one at that time,
        /// otherwise one within 50 ms, among the candidates that are 'awaiting' an answer.
        /// </summary>
        internal TimingCandidate FindCandidateFor(TimeSpan startTime, Func<TimingCandidate, bool> awaiting)
        {
            if (r_byOriginalStart.TryGetValue(startTime, out var exact) && awaiting(exact))
                return exact;

            return Candidates.FirstOrDefault(c =>
                awaiting(c) &&
                Math.Abs((c.OriginalStartTime - startTime).TotalMilliseconds) < 50);
        }

        // ---------------------------------------------------------------------
        // Text and silence (the shared settings)
        // ---------------------------------------------------------------------

        /// <summary>The same text, or (when TextMismatchMinSimilarity is set) similar enough not to be a mismatch.</summary>
        internal bool IsSameText(string sent, string heard)
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
        internal TimeSpan TrimmedTime(TimingCandidate cand, BoundarySide side, TimeSpan t)
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
        internal void TrimSilence(TimingCandidate cand, BoundarySide side)
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

        /// <summary>
        /// How many dips the final boundary is from the initial (snapped) one: positive = outward (Start earlier / End later),
        /// negative = inward. Null when the boundary has no dips (nothing was snapped on the terrain).
        /// </summary>
        public static int? DipDelta(TimingCandidate cand, BoundarySide side)
        {
            var boundary = cand[side];
            if (boundary.Dips.Count == 0)
                return null;

            var times = boundary.Dips.Select(d => d.Time).OrderBy(t => t).ToList();
            int NearestIndex(TimeSpan t)
            {
                int best = 0;
                for (int i = 1; i < times.Count; i++)
                    if (Math.Abs((times[i] - t).Ticks) < Math.Abs((times[best] - t).Ticks))
                        best = i;
                return best;
            }

            int delta = NearestIndex(boundary.Current) - NearestIndex(boundary.Init);
            return side == BoundarySide.Start ? -delta : delta;
        }

        /// <summary>Adds a last trace row to each candidate: the change of its boundaries compared to the initial snap, in dips.</summary>
        public void AddFinalDipTraces()
        {
            foreach (var cand in Candidates.Where(c => c.Origin != TimingOrigin.TitleCard))
            {
                var start = DipDelta(cand, BoundarySide.Start);
                var end = DipDelta(cand, BoundarySide.End);
                if (start == null && end == null)
                    continue;

                string Fmt(int? v) => v.HasValue ? v.Value.ToString("+0;-0;0") : "?";
                cand.Traces.Add($"{cand.GetLockTag()} {"Dips",-4} Final vs Init: Start= {Fmt(start)}, End= {Fmt(end)}");
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

            foreach (var c in subs)
            {
                var startDelta = DipDelta(c, BoundarySide.Start);
                var endDelta = DipDelta(c, BoundarySide.End);
                if (startDelta.HasValue)
                    stats.StartDipDeltas[startDelta.Value] = stats.StartDipDeltas.TryGetValue(startDelta.Value, out var sn) ? sn + 1 : 1;
                if (endDelta.HasValue)
                    stats.EndDipDeltas[endDelta.Value] = stats.EndDipDeltas.TryGetValue(endDelta.Value, out var en) ? en + 1 : 1;
            }

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
