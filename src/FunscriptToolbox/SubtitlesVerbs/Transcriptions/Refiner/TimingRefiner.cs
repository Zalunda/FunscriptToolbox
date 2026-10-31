using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>
    /// Refines the timing of transcription candidates by generating multi-clip comparative options
    /// (Max, M{N}..M1, Init, P1..P{N}) around terrain dips and asking the AI to judge the best Start and End boundaries.
    ///
    /// Internally the levels are stored in arrays: index 0 = tightest (M{N}), index N = Init, index 2N = widest (P{N}).
    /// </summary>
    public class TimingRefiner
    {
        // ---------------------------------------------------------------------
        // Constants
        // ---------------------------------------------------------------------

        private const float SpeechRatio = 4f;
        private const float PeakRatio = 6f;

        private static readonly TimeSpan MinJump = TimeSpan.FromSeconds(0.07);
        private static readonly TimeSpan MinClipDuration = TimeSpan.FromSeconds(0.05);
        private static readonly TimeSpan MinOverlapViableDuration = TimeSpan.FromSeconds(0.1);
        private static readonly TimeSpan MaxSnapDrift = TimeSpan.FromSeconds(1.2);
        private static readonly TimeSpan MaxFinalClampOverlap = TimeSpan.FromMilliseconds(150);
        private const string MaxOptionKey = "Max";
        private const string MinOptionKey = "Min";

        // ---------------------------------------------------------------------
        // Settings & state
        // ---------------------------------------------------------------------

        public int MaxRetriesPerNode { get; }
        public int MaxMountainStepsInward { get; }
        public int MaxMountainStepsOutward { get; }
        public TimeSpan MaxExpansion { get; }
        public TimeSpan BleedDetectionMargin { get; }
        public TimeSpan TinyOverlapTolerance { get; }
        public TimeSpan MinSubtitleDuration { get; }

        public AudioTerrainMapping TerrainMapping { get; }
        public TimeSpan AudioEndTime { get; }

        /// <summary>Always kept sorted by OriginalStartTime.</summary>
        public List<TimingCandidate> Candidates { get; private set; } = new List<TimingCandidate>();

        private readonly Dictionary<TimeSpan, TimingCandidate> r_byOriginalStart = new Dictionary<TimeSpan, TimingCandidate>();

        private TimeSpan TinyOverlapThreshold =>
            TimeSpan.FromMilliseconds(Math.Max(60, TinyOverlapTolerance.TotalMilliseconds * 2));

        public TimingRefiner(PcmAudio fullPcmAudioForRefiner, ITimingRefinerSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            MaxRetriesPerNode = settings.MaxRetriesPerNode;
            MaxMountainStepsInward = Math.Max(0, settings.MaxMountainStepsInward);
            MaxMountainStepsOutward = Math.Max(0, settings.MaxMountainStepsOutward);
            MaxExpansion = settings.MaxExpansion;
            BleedDetectionMargin = settings.BleedDetectionMargin;
            TinyOverlapTolerance = settings.TinyOverlapTolerance;
            AudioEndTime = fullPcmAudioForRefiner?.EndTime ?? TimeSpan.MaxValue;

            TerrainMapping = new AudioTerrainMapping(fullPcmAudioForRefiner)
            {
                SpeechRatio = SpeechRatio,
                PeakRatio = PeakRatio,
                ValleyReach = settings.ValleyReach,
            };
            TerrainMapping.Compute();
        }

        // ---------------------------------------------------------------------
        // Small helpers
        // ---------------------------------------------------------------------

        private static TimeSpan Shift(TimeSpan t, BoundarySide side, MoveDirection dir, TimeSpan amount)
            => AudioTerrainMapping.MovesLater(side, dir) ? t + amount : t - amount;

        private static TimeSpan Midpoint(TimeSpan a, TimeSpan b)
            => TimeSpan.FromMilliseconds((a.TotalMilliseconds + b.TotalMilliseconds) / 2.0);

        private static bool IsMaxKey(string key)
            => string.Equals(key, MaxOptionKey, StringComparison.OrdinalIgnoreCase);

        private static bool IsMinKey(string key)
            => string.Equals(key, MinOptionKey, StringComparison.OrdinalIgnoreCase);

        private static bool IsSpecialKey(string key)
            => IsMaxKey(key) || IsMinKey(key);

        private TimeSpan ClampToAudioBounds(TimeSpan t)
        {
            if (t < TimeSpan.Zero)
                return TimeSpan.Zero;
            if (t > AudioEndTime)
                return AudioEndTime;
            return t;
        }

        // ---------------------------------------------------------------------
        // Candidate creation (initial snapping)
        // ---------------------------------------------------------------------

        public void AddCandidate(TimedItemWithMetadata originalItem, string originalText)
        {
            var candidate = new TimingCandidate
            {
                OriginalStartTime = originalItem.StartTime,
                OriginalEndTime = originalItem.EndTime,
                OriginalItem = originalItem,
                OriginalText = originalText,
            };

            TimeSpan start = SnapInitialBoundary(candidate, BoundarySide.Start);
            TimeSpan end = SnapInitialBoundary(candidate, BoundarySide.End);

            if (end <= start)
            {
                start = originalItem.StartTime;
                end = originalItem.EndTime;
            }

            EnforceMinimumDuration(candidate, ref start, ref end);

            candidate.Start.Init = start;
            candidate.Start.Current = start;
            candidate.End.Init = end;
            candidate.End.Current = end;

            candidate.AddTraceRow("Orig", ' ', originalItem.StartTime, originalItem.EndTime, ' ', "", candidate.OriginalText);
            candidate.Trace("Init", originalItem.StartTime, originalItem.EndTime, "Snapped initial bounds");

            InitializeCandidateDips(candidate);

            Candidates.Add(candidate);
            if (!r_byOriginalStart.ContainsKey(candidate.OriginalStartTime))
                r_byOriginalStart.Add(candidate.OriginalStartTime, candidate);

            // Keep sorted by construction (normally a no-op since the caller adds them in order)
            if (Candidates.Count > 1 && candidate.OriginalStartTime < Candidates[Candidates.Count - 2].OriginalStartTime)
                Candidates = Candidates.OrderBy(c => c.OriginalStartTime).ToList();
        }

        private void InitializeCandidateDips(TimingCandidate cand)
        {
            var centerStart = cand.Start.Current;
            var centerEnd = cand.End.Current;

            // Start dips: from centerEnd - MinClipDuration outward to startMax
            var startMax = ComputeMax(cand, BoundarySide.Start, centerStart, centerEnd);
            var startInwardLimit = centerEnd - MinClipDuration;
            cand.Start.Dips.Clear();
            foreach (var t in TerrainMapping.GetDips(BoundarySide.Start, startInwardLimit, startMax))
                cand.Start.Dips.Add(new BoundaryDipCandidate(t));

            // Ensure current snapped start is present in the list
            EnsureDipPresent(cand.Start.Dips, centerStart);

            // End dips: from centerStart + MinClipDuration outward to endMax
            var endMax = ComputeMax(cand, BoundarySide.End, centerStart, centerEnd);
            var endInwardLimit = centerStart + MinClipDuration;
            cand.End.Dips.Clear();
            foreach (var t in TerrainMapping.GetDips(BoundarySide.End, endInwardLimit, endMax))
                cand.End.Dips.Add(new BoundaryDipCandidate(t));

            // Ensure current snapped end is present in the list
            EnsureDipPresent(cand.End.Dips, centerEnd);
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
        // Round preparation
        // ---------------------------------------------------------------------

        /// <summary>
        /// Prepares the next round of clip options (Audio_Max_Max first, then tightest to widest)
        /// and neighbour bleed metadata for all unlocked candidates.
        /// </summary>
        public void PrepareNextRound()
        {
            for (int i = 0; i < Candidates.Count; i++)
            {
                var cand = Candidates[i];
                if (cand.IsFullyLocked || cand.RetryCount >= MaxRetriesPerNode)
                {
                    cand.CurrentRound = null;
                    continue;
                }

                cand.PossibleBleedFromPrev = i > 0
                    ? BleedTextFrom(Candidates[i - 1], cand.Start.Current - Candidates[i - 1].End.Current)
                    : null;
                cand.PossibleBleedFromNext = i < Candidates.Count - 1
                    ? BleedTextFrom(Candidates[i + 1], Candidates[i + 1].Start.Current - cand.End.Current)
                    : null;

                cand.CurrentRound = BuildRoundForCandidate(cand);
            }
        }

        private string BleedTextFrom(TimingCandidate neighbour, TimeSpan gap)
            => gap <= BleedDetectionMargin && !string.IsNullOrWhiteSpace(neighbour.OriginalText)
                ? neighbour.OriginalText
                : null;

        private TimingRound BuildRoundForCandidate(TimingCandidate cand)
        {
            var centerStart = cand.Start.Current;
            var centerEnd = cand.End.Current;

            var round = new TimingRound
            {
                RoundNumber = cand.RetryCount + 1,
                CenterStart = centerStart,
                CenterEnd = centerEnd
            };

            var startMax = ComputeMax(cand, BoundarySide.Start, centerStart, centerEnd);
            var endMax = ComputeMax(cand, BoundarySide.End, centerStart, centerEnd);

            // Select active (non-eliminated) dips for each side
            var startDips = SelectActiveDips(cand, BoundarySide.Start, centerStart, centerEnd - MinClipDuration, startMax);
            var endDips = SelectActiveDips(cand, BoundarySide.End, centerEnd, centerStart + MinClipDuration, endMax);

            var windows = BuildWindowsFromDips(startDips, endDips, centerStart, centerEnd);

            // Neutral labels, ordered from tightest (innermost) to widest (outermost)
            var startOrder = LabelOptions(BoundarySide.Start, windows.Select(w => w.Start), round);
            var endOrder = LabelOptions(BoundarySide.End, windows.Select(w => w.End), round);

            // Clamp Max to widest option if trailing terrain is silence
            startMax = TrimSilentMargin(BoundarySide.Start, startMax, startOrder[startOrder.Count - 1]);
            endMax = TrimSilentMargin(BoundarySide.End, endMax, endOrder[endOrder.Count - 1]);
            round.StartOptions[MaxOptionKey] = startMax;
            round.EndOptions[MaxOptionKey] = endMax;

            // Min option: only offered when non-eliminated dips exist inward beyond the tightest candidate
            if (HasActiveInwardDip(cand, BoundarySide.Start, startOrder[0]))
                round.StartOptions[MinOptionKey] = startOrder[0];
            if (HasActiveInwardDip(cand, BoundarySide.End, endOrder[0]))
                round.EndOptions[MinOptionKey] = endOrder[0];

            AddClips(round, windows, startOrder, endOrder, startMax, endMax);

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
                ? inward.OrderBy(t => t).Take(MaxMountainStepsInward)
                : inward.OrderByDescending(t => t).Take(MaxMountainStepsInward);

            var outward = available
                .Where(t => isStart ? t < center : t > center);
            outward = isStart
                ? outward.OrderByDescending(t => t).Take(MaxMountainStepsOutward)
                : outward.OrderBy(t => t).Take(MaxMountainStepsOutward);

            return inward.Concat(new[] { center }).Concat(outward).Distinct().OrderBy(t => t).ToList();
        }

        private static bool HasActiveInwardDip(TimingCandidate cand, BoundarySide side, TimeSpan tightestTime)
        {
            if (cand[side].Locked)
                return false;

            bool isStart = side == BoundarySide.Start;
            return cand[side].Dips.Any(d => d.State != DipCandidateState.Eliminated && d.State != DipCandidateState.HardLimit && (isStart ? d.Time > tightestTime : d.Time < tightestTime));
        }

        private List<(TimeSpan Start, TimeSpan End)> BuildWindowsFromDips(
            List<TimeSpan> startDips,
            List<TimeSpan> endDips,
            TimeSpan centerStart,
            TimeSpan centerEnd)
        {
            var starts = startDips.OrderByDescending(t => t).ToList(); // Tightest to widest
            var ends = endDips.OrderBy(t => t).ToList();

            var windows = new List<(TimeSpan Start, TimeSpan End)>();
            int count = Math.Max(starts.Count, ends.Count);

            for (int i = 0; i < count; i++)
            {
                var s = i < starts.Count ? starts[i] : starts.Last();
                var e = i < ends.Count ? ends[i] : ends.Last();

                if (e - s < MinClipDuration)
                {
                    // If tightest pair is too short, expand toward center to maintain minimum duration
                    if (e - centerStart >= MinClipDuration)
                        s = centerStart;
                    else if (centerEnd - s >= MinClipDuration)
                        e = centerEnd;
                    else
                    {
                        s = centerStart;
                        e = centerEnd;
                    }
                }

                if (e - s >= MinClipDuration && !windows.Contains((s, e)))
                    windows.Add((s, e));
            }

            if (windows.Count == 0)
            {
                var fallbackEnd = centerEnd > centerStart + MinClipDuration ? centerEnd : centerStart + MinClipDuration;
                windows.Add((centerStart, ClampToAudioBounds(fallbackEnd)));
            }

            return windows.OrderByDescending(w => w.Start).ThenBy(w => w.End).ToList();
        }

        /// <summary>Furthest the boundary may go outward (limited by MaxExpansion, audio bounds and neighbours' hard limit).</summary>
        private TimeSpan ComputeMax(TimingCandidate cand, BoundarySide side, TimeSpan centerStart, TimeSpan centerEnd)
        {
            var hardLimit = GetHardLimit(cand, side);

            if (side == BoundarySide.Start)
            {
                var max = ClampToAudioBounds(centerStart - MaxExpansion);
                return hardLimit.HasValue && max < hardLimit.Value ? hardLimit.Value : max;
            }

            var endMax = ClampToAudioBounds(centerEnd + MaxExpansion);
            if (hardLimit.HasValue && endMax > hardLimit.Value)
                endMax = hardLimit.Value;
            if (endMax <= centerStart)
                endMax = ClampToAudioBounds(centerStart + MinClipDuration);
            return endMax;
        }

        /// <summary>
        /// Registers S1..SK (start) or E1..EK (end) in round.StartOptions/EndOptions and returns the ordered times (index 1 first).
        /// Start: latest start = tightest (S1). End: earliest end = tightest (E1).
        /// </summary>
        private static List<TimeSpan> LabelOptions(BoundarySide side, IEnumerable<TimeSpan> times, TimingRound round)
        {
            var distinct = times.Distinct();
            var ordered = (side == BoundarySide.Start
                    ? distinct.OrderByDescending(t => t)
                    : distinct.OrderBy(t => t)).ToList();

            var options = round.OptionsFor(side);
            for (int i = 0; i < ordered.Count; i++)
                options[OptionKey(side, i + 1)] = ordered[i];
            return ordered;
        }

        private static string OptionId(BoundarySide side, List<TimeSpan> ordered, TimeSpan time)
            => OptionKey(side, ordered.IndexOf(time) + 1);
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

        private static void AddClips(
            TimingRound round,
            List<(TimeSpan Start, TimeSpan End)> windows,
            List<TimeSpan> startOrder,
            List<TimeSpan> endOrder,
            TimeSpan startMax,
            TimeSpan endMax)
        {
            // 1. Audio_Max_Max FIRST so the AI listens to the wide reference window first
            if (endMax - startMax >= MinClipDuration)
            {
                round.Clips.Add(new TimingClipOption
                {
                    FieldName = "Audio_Max_Max",
                    StartId = MaxOptionKey,
                    EndId = MaxOptionKey,
                    Start = startMax,
                    End = endMax
                });
            }

            // 2. Audio_C1_C1 .. from tightest to widest
            foreach (var (s, e) in windows)
            {
                string sId = OptionId(BoundarySide.Start, startOrder, s);
                string eId = OptionId(BoundarySide.End, endOrder, e);
                string fieldName = $"Audio_{sId}_{eId}";   // e.g. Audio_S2_E3

                if (round.Clips.Any(c => string.Equals(c.FieldName, fieldName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                round.Clips.Add(new TimingClipOption
                {
                    FieldName = fieldName,
                    StartId = sId,
                    EndId = eId,
                    Start = s,
                    End = e
                });
            }
        }

        private static string FormatDips(TimingCandidate cand, BoundarySide side)
        {
            var initTime = cand[side].Init;
            return string.Join(" ", cand[side].Dips.Select(d => d.Format(initTime)));
        }

        // ---------------------------------------------------------------------
        // AI response processing
        // ---------------------------------------------------------------------

        public void ProcessBatchResults(IEnumerable<TranscribedItem> responseItems)
        {
            foreach (var item in responseItems)
            {
                var cand = FindCandidateFor(item);
                if (cand != null)
                    ApplyResponse(cand, item);
            }

            CoordinateNeighborOverlaps();
        }

        private TimingCandidate FindCandidateFor(TranscribedItem item)
        {
            if (r_byOriginalStart.TryGetValue(item.StartTime, out var exact) && exact.CurrentRound != null)
                return exact;

            return Candidates.FirstOrDefault(c =>
                c.CurrentRound != null &&
                Math.Abs((c.OriginalStartTime - item.StartTime).TotalMilliseconds) < 50);
        }

        private static string GetTrimmedMeta(TranscribedItem item, string key)
        {
            item.Metadata.TryGetValue(key, out var value);
            return value?.Trim();
        }

        private void ApplyResponse(TimingCandidate cand, TranscribedItem item)
        {
            var round = cand.CurrentRound;
            cand.RetryCount++;

            round.BestStart = GetTrimmedMeta(item, TimingMeta.BestStart);
            round.BestEnd = GetTrimmedMeta(item, TimingMeta.BestEnd);
            round.HeardText = GetTrimmedMeta(item, TimingMeta.HeardText);
            cand.Rounds.Add(round);
            cand.CurrentRound = null;

            var prevStart = cand.Start.Current;
            var prevEnd = cand.End.Current;

            if (IsInaudibleToken(round.BestStart) || IsInaudibleToken(round.BestEnd))
            {
                cand.LockAll();
                cand.AddValidationNote($"Unsure: Audio Inaudible (original): {cand.OriginalText}");
                cand.Trace($"#{cand.RetryCount}", prevStart, prevEnd, "AI: Inaudible", round.HeardText);
                return;
            }

            foreach (var side in TimingCandidate.Sides)
                ApplySideChoice(cand, round, side);

            // Guard against crossed boundaries
            if (cand.End.Current <= cand.Start.Current)
            {
                cand.Start.Current = round.CenterStart;
                cand.End.Current = round.CenterEnd;
                cand.LockAll();
                cand.AddValidationNote("Unsure: Boundaries crossed, kept previous timing");
            }

            // Record HeardText note if AI reported a different transcription in Max
            if (!string.IsNullOrWhiteSpace(round.HeardText) &&
                !string.Equals(round.HeardText, "null", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(round.HeardText, cand.OriginalText, StringComparison.OrdinalIgnoreCase))
            {
                cand.AddValidationNote($"Unsure: Text Mismatch (original vs heard in Max): `{cand.OriginalText}` vs `{round.HeardText}`");
            }

            cand.Traces.Add($"#{round.RoundNumber} Options:");
            cand.Traces.Add($"     START: {FormatDips(cand, BoundarySide.Start)}");
            cand.Traces.Add($"     END:   {FormatDips(cand, BoundarySide.End)}");
            cand.Trace($"#{cand.RetryCount}", prevStart, prevEnd, null, round.HeardText);
        }

        private void ApplySideChoice(TimingCandidate cand, TimingRound round, BoundarySide side)
        {
            var boundary = cand[side];
            if (boundary.Locked)
                return;

            var (time, locked, needsMore) = ResolveBoundaryChoice(cand, round, side, round.ChoiceFor(side));
            boundary.Current = time;
            boundary.Locked = locked;

            if (needsMore && (locked || cand.RetryCount >= MaxRetriesPerNode))
            {
                boundary.Locked = true;
                cand.AddValidationNote($"Unsure: Missing {side} (Max expansion reached)");
            }
        }

        private static bool IsInaudibleToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;
            return token.IndexOf("Inaudible", StringComparison.OrdinalIgnoreCase) >= 0
                || token.IndexOf("None", StringComparison.OrdinalIgnoreCase) >= 0
                || token.IndexOf("Silence", StringComparison.OrdinalIgnoreCase) >= 0;
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
            bool isStart = side == BoundarySide.Start;
            var options = round.OptionsFor(side);
            var fallback = round.CenterFor(side);

            if (string.IsNullOrWhiteSpace(rawChoice))
                return (fallback, true, false);

            string token = NormalizeToken(rawChoice, isStart);

            if (string.Equals(token, MaxOptionKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "LastChance", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveMaxChoice(cand, side, options, fallback);
            }

            if (string.Equals(token, MinOptionKey, StringComparison.OrdinalIgnoreCase))
            {
                return ResolveMinChoice(cand, side, options, fallback);
            }

            // Hyphenated pair e.g. "C2-C3": prefer the tighter boundary rather than cutting in the middle of a mountain
            if (token.Contains("-"))
            {
                var pair = token.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
                if (pair.Length == 2 &&
                    TryLookupOptionTime(options, pair[0].Trim(), fallback, out var t1) &&
                    TryLookupOptionTime(options, pair[1].Trim(), fallback, out var t2))
                {
                    var tighter = isStart ? (t1 > t2 ? t1 : t2) : (t1 < t2 ? t1 : t2);
                    MarkDipsAfterChoice(cand, side, options, tighter);
                    return (tighter, true, false);
                }
            }

            if (TryLookupOptionTime(options, token, fallback, out var resolved))
            {
                MarkDipsAfterChoice(cand, side, options, resolved);
                return (resolved, true, false);
            }

            MarkDipsAfterChoice(cand, side, options, fallback);
            return (fallback, true, false);
        }

        /// <summary>Strips the optional "Audio_X_Y" prefix, legacy "+30ms" suffix and trailing '*'.</summary>
        private static string NormalizeToken(string rawChoice, bool isStart)
        {
            string token = rawChoice.Trim();

            if (token.StartsWith("Audio_", StringComparison.OrdinalIgnoreCase))
            {
                var parts = token.Split('_');
                if (parts.Length >= 3)
                    token = isStart ? parts[1] : parts[2];
            }

            if (token.EndsWith("+30ms", StringComparison.OrdinalIgnoreCase))
                token = token.Substring(0, token.Length - 5).Trim();
            else if (token.EndsWith("+30", StringComparison.OrdinalIgnoreCase))
                token = token.Substring(0, token.Length - 3).Trim();

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

        private static void MarkDipsAfterChoice(
            TimingCandidate cand,
            BoundarySide side,
            Dictionary<string, TimeSpan> options,
            TimeSpan resolved)
        {
            var testedTimes = new HashSet<TimeSpan>(options.Values);

            foreach (var d in cand[side].Dips)
            {
                if (d.State == DipCandidateState.HardLimit)
                    continue;

                if (d.Time == resolved)
                {
                    d.State = DipCandidateState.Selected;
                }
                else if (testedTimes.Contains(d.Time) && d.State != DipCandidateState.Selected)
                {
                    d.State = DipCandidateState.Eliminated;
                }
            }
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

            EliminateDips(cand, side, widestC, inward: true);

            // 1. Max == widest candidate (rest of the zone was silence or clamped): lock cleanly
            if (maxOpt == widestC)
            {
                MarkDipSelected(cand, side, widestC);
                return (widestC, true, false);
            }

            // 2. Max is already clamped against a neighbour's HardLimit: lock cleanly
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

        /// <summary>
        /// The AI answered "Min". We move the center to the tightest candidate option and retry inward,
        /// marking all dips at or outside the tightest candidate as eliminated.
        /// </summary>
        private (TimeSpan, bool, bool) ResolveMinChoice(
            TimingCandidate cand,
            BoundarySide side,
            Dictionary<string, TimeSpan> options,
            TimeSpan fallback)
        {
            bool isStart = side == BoundarySide.Start;
            var tightestC = GetTightestCandidateTime(options, isStart, fallback);

            EliminateDips(cand, side, tightestC, inward: false);

            // Tightest did not advance beyond fallback (no dips existed): lock cleanly
            if (tightestC == fallback)
            {
                MarkDipSelected(cand, side, tightestC);
                return (tightestC, true, false);
            }

            return (tightestC, false, true);
        }

        private static void EliminateDips(TimingCandidate cand, BoundarySide side, TimeSpan boundaryTime, bool inward)
        {
            bool isStart = side == BoundarySide.Start;
            foreach (var d in cand[side].Dips)
            {
                if (d.State == DipCandidateState.HardLimit)
                    continue;

                bool shouldEliminate = inward
                    ? (isStart ? d.Time >= boundaryTime : d.Time <= boundaryTime)
                    : (isStart ? d.Time <= boundaryTime : d.Time >= boundaryTime);

                if (shouldEliminate && d.State != DipCandidateState.Selected)
                    d.State = DipCandidateState.Eliminated;
            }
        }

        private static TimeSpan GetWidestCandidateTime(Dictionary<string, TimeSpan> options, bool isStart, TimeSpan fallback)
        {
            var vals = GetOptionTimes(options);
            return vals.Count == 0 ? fallback : (isStart ? vals.Min() : vals.Max());
        }

        private static TimeSpan GetTightestCandidateTime(Dictionary<string, TimeSpan> options, bool isStart, TimeSpan fallback)
        {
            var vals = GetOptionTimes(options);
            return vals.Count == 0 ? fallback : (isStart ? vals.Max() : vals.Min());
        }

        private static List<TimeSpan> GetOptionTimes(Dictionary<string, TimeSpan> options) =>
            options.Where(kvp => !IsSpecialKey(kvp.Key)).Select(kvp => kvp.Value).ToList();

        private static bool TryLookupOptionTime(Dictionary<string, TimeSpan> options, string key, TimeSpan centerFallback, out TimeSpan time)
        {
            if (options.TryGetValue(key, out time))
                return true;

            // Normalize common aliases if the AI ever outputs Init
            if (string.Equals(key, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "Init", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "Initial", StringComparison.OrdinalIgnoreCase))
            {
                time = centerFallback;
                return true;
            }

            return false;
        }

        // ---------------------------------------------------------------------
        // Neighbour overlap coordination
        // ---------------------------------------------------------------------

        /// <summary>
        /// Resolves overlaps between adjacent candidates at the end of an iteration:
        /// - No overlap: propagate hard limits from locked boundaries.
        /// - Tiny overlap (&lt;= TinyOverlapThreshold): clamp cleanly.
        /// - Case A (cur still has a valid End dip at or before next.Start): next.Start overrules cur.End.
        /// - Case B (next.Start jumped onto/past cur's mountain, no valid End dip for cur):
        ///   lock cur, set next.HardLimit.Start = cur.End and reset next for a rerun.
        /// </summary>
        private void CoordinateNeighborOverlaps()
        {
            for (int i = 0; i < Candidates.Count - 1; i++)
            {
                var cur = Candidates[i];
                var next = Candidates[i + 1];

                if (cur.End.Current <= next.Start.Current)
                {
                    PropagateHardLimits(cur, next);
                    continue;
                }

                if (TryClampTinyOverlap(cur, next))
                    continue;

                if (TryFindBestEndAtOrBefore(cur, next.Start.Current, out var fallbackEnd))
                    CapCurrentEnd(cur, next, fallbackEnd);
                else
                    ResetNextAfterStolenMountain(cur, next);
            }
        }

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

        private static void SetHardLimit(TimingCandidate cand, BoundarySide side, TimeSpan limit)
        {
            EnsureDipPresent(cand[side].Dips, limit);
            bool isStart = side == BoundarySide.Start;
            foreach (var d in cand[side].Dips)
            {
                if (isStart ? d.Time < limit : d.Time > limit)
                    d.State = DipCandidateState.HardLimit;
            }
        }

        private static void PropagateHardLimits(TimingCandidate cur, TimingCandidate next)
        {
            if (next.Start.Locked)
                SetHardLimit(cur, BoundarySide.End, next.Start.Current);
            if (cur.End.Locked)
                SetHardLimit(next, BoundarySide.Start, cur.End.Current);
        }

        /// <summary>Tiny overlap (e.g. from a shared valley): clamp at midpoint or next.Start.</summary>
        private bool TryClampTinyOverlap(TimingCandidate cur, TimingCandidate next)
        {
            var overlap = cur.End.Current - next.Start.Current;
            if (overlap > TinyOverlapThreshold || next.Start.Current <= cur.Start.Current + MinOverlapViableDuration)
                return false;

            var clampPoint = next.Start.Locked
                ? next.Start.Current
                : Midpoint(cur.End.Current, next.Start.Current);

            if (clampPoint - cur.Start.Current < MinOverlapViableDuration ||
                next.End.Current - clampPoint < MinOverlapViableDuration)
                return false;

            var prevEnd = cur.End.Current;
            cur.End.Current = clampPoint;
            if (!next.Start.Locked)
                next.Start.Current = clampPoint;

            cur.Trace("Clmp", cur.Start.Current, prevEnd, $"Clamped tiny overlap with next ({clampPoint:mm\\:ss\\.fff})");
            return true;
        }

        /// <summary>Case A: next.Start overrules cur.End; cur.End is pulled back and locked.</summary>
        private void CapCurrentEnd(TimingCandidate cur, TimingCandidate next, TimeSpan fallbackEnd)
        {
            var hardCap = next.Start.Current;
            var currentHardLimit = GetHardLimit(cur, BoundarySide.End);
            bool alreadyCappedHere = cur.End.Locked && cur.End.Current == fallbackEnd && currentHardLimit == hardCap;

            SetHardLimit(cur, BoundarySide.End, hardCap);
            var prevEnd = cur.End.Current;
            cur.End.Current = fallbackEnd;

            MarkDipSelected(cur, BoundarySide.End, fallbackEnd);
            cur.End.Locked = true;

            if (!alreadyCappedHere)
                cur.Trace("CapE", cur.Start.Current, prevEnd, $"Overruled by next Start ({hardCap:mm\\:ss\\.fff})");
        }

        /// <summary>Case B: lock cur, fence next behind cur.End, reset next's bounds and rerun it.</summary>
        private void ResetNextAfterStolenMountain(TimingCandidate cur, TimingCandidate next)
        {
            cur.LockAll();

            var minNextStart = cur.End.Current;
            var currentNextHardLimit = GetHardLimit(next, BoundarySide.Start);
            bool alreadyResetHere = currentNextHardLimit == minNextStart && next.Start.Current >= minNextStart;
            SetHardLimit(next, BoundarySide.Start, minNextStart);

            var prevStart = next.Start.Current;
            var prevEnd = next.End.Current;

            TimeSpan resetStart = next.Start.Init >= minNextStart
                ? next.Start.Init
                : FindFirstStartAtOrAfter(minNextStart);

            TimeSpan resetEnd = next.End.Init;
            if (resetEnd <= resetStart + MinOverlapViableDuration)
            {
                resetEnd = next.End.Current > resetStart + MinOverlapViableDuration
                    ? next.End.Current
                    : FindFirstEndAfter(resetStart, MinSubtitleDuration);
            }

            next.Start.Current = resetStart;
            next.End.Current = resetEnd;

            if (!next.WasResetByOverlap && next.RetryCount >= MaxRetriesPerNode)
                next.RetryCount = Math.Max(0, MaxRetriesPerNode - 1);
            next.WasResetByOverlap = true;

            if (next.RetryCount < MaxRetriesPerNode)
            {
                next.UnlockAll();
                next.Trace("Rset", prevStart, prevEnd, $"Overlapped prev End ({minNextStart:mm\\:ss\\.fff}) -> reset & retry");
            }
            else
            {
                next.LockAll();
                if (!alreadyResetHere)
                    next.Trace("Rset", prevStart, prevEnd, $"Overlapped prev End ({minNextStart:mm\\:ss\\.fff}) -> reset");
            }
        }

        /// <summary>Distinct End options of the round (excluding Max) that lie within (minEnd, hardCap].</summary>
        private static IEnumerable<TimeSpan> ValidEndOptions(TimingRound round, TimeSpan hardCap, TimeSpan minEnd)
        {
            if (round == null)
                return Enumerable.Empty<TimeSpan>();

            return round.EndOptions
                .Where(kvp => !IsMaxKey(kvp.Key) && kvp.Value <= hardCap && kvp.Value > minEnd)
                .Select(kvp => kvp.Value)
                .Distinct();
        }

        private bool TryFindBestEndAtOrBefore(TimingCandidate cand, TimeSpan hardCap, out TimeSpan bestEnd)
        {
            bestEnd = cand.End.Current;
            var minEnd = cand.Start.Current + MinOverlapViableDuration;
            if (hardCap <= minEnd)
                return false;

            // 1. Latest End option of the last round that fits
            var fromOptions = ValidEndOptions(cand.Rounds.LastOrDefault(), hardCap, minEnd)
                .OrderByDescending(t => t)
                .ToList();
            if (fromOptions.Count > 0)
            {
                bestEnd = fromOptions[0];
                return true;
            }

            // 2. A terrain dip just before the cap
            var dip = TerrainMapping.FindDip(hardCap, BoundarySide.End, MoveDirection.Inward, new DipSearch
            {
                Inclusive = true,
                StopAtSpeechEdge = true,
                Limit = minEnd
            });
            if (dip.HasValue && dip.Value <= hardCap && dip.Value > minEnd)
            {
                bestEnd = dip.Value;
                return true;
            }

            // 3. The initial End
            if (cand.End.Init <= hardCap && cand.End.Init > minEnd)
            {
                bestEnd = cand.End.Init;
                return true;
            }

            return false;
        }

        private TimeSpan FindFirstStartAtOrAfter(TimeSpan minStart)
        {
            var dip = TerrainMapping.FindDip(minStart, BoundarySide.Start, MoveDirection.Inward, new DipSearch
            {
                Inclusive = true,
                StopAtSpeechEdge = true
            });
            return dip.HasValue && dip.Value >= minStart ? dip.Value : minStart;
        }

        private TimeSpan FindFirstEndAfter(TimeSpan start, TimeSpan minDuration)
        {
            var target = ClampToAudioBounds(start + minDuration);
            var dip = TerrainMapping.FindDip(target, BoundarySide.End, MoveDirection.Outward, new DipSearch
            {
                Inclusive = true,
                StopAtSpeechEdge = true
            });
            return dip.HasValue && dip.Value > start ? dip.Value : target;
        }

        // ---------------------------------------------------------------------
        // Finalization & stats
        // ---------------------------------------------------------------------

        public void FinalizeOverlapsAndMinimumDuration()
        {
            for (int i = 0; i < Candidates.Count - 1; i++)
            {
                var cur = Candidates[i];
                var next = Candidates[i + 1];

                if (cur.End.Current <= next.Start.Current)
                    continue;

                // Slight overlap (e.g. shared valley): clamp at midpoint
                var overlap = cur.End.Current - next.Start.Current;
                if (overlap <= MaxFinalClampOverlap)
                {
                    var mid = Midpoint(cur.End.Current, next.Start.Current);
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

                cur.AddValidationNote("Review: Overlaps with adjacent subtitle");
                next.AddValidationNote("Review: Overlaps with adjacent subtitle");
            }
        }

        /// <summary>Computes the summary numbers (null when there are no candidates).</summary>
        public TimingRefinerStats ComputeStats()
        {
            if (Candidates.Count == 0)
                return null;

            var stats = new TimingRefinerStats
            {
                Total = Candidates.Count,
                ResolvedInOneRound = Candidates.Count(c => c.Rounds.Count <= 1 && c.IsFullyLocked),
                Unfinished = Candidates.Count(c => !c.IsFullyLocked),
                Review = Candidates.Count(c => c.ValidationNotes.Count > 0 || !c.IsFullyLocked),
            };
            stats.Clean = stats.Total - stats.Review;

            foreach (var (key, _) in TimingRefinerStats.NoteCategories)
            {
                stats.NoteCounts[key] = Candidates.Count(c =>
                    c.ValidationNotes.Any(n => n.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            stats.AvgOriginalDuration = Candidates.Average(c => (c.OriginalEndTime - c.OriginalStartTime).TotalSeconds);
            stats.AvgFinalDuration = Candidates.Average(c => (c.End.Current - c.Start.Current).TotalSeconds);
            stats.AvgStartDrift = Candidates.Average(c => (c.Start.Current - c.OriginalStartTime).TotalSeconds);
            stats.MaxOutwardStartDrift = Candidates.Min(c => (c.Start.Current - c.OriginalStartTime).TotalSeconds);
            stats.AvgEndDrift = Candidates.Average(c => (c.End.Current - c.OriginalEndTime).TotalSeconds);
            stats.MaxOutwardEndDrift = Candidates.Max(c => (c.End.Current - c.OriginalEndTime).TotalSeconds);

            for (int i = 0; i < Candidates.Count; i++)
            {
                if (Candidates[i].End.Current - Candidates[i].Start.Current <= MinClipDuration)
                    stats.Squashed++;
                if (i < Candidates.Count - 1 && Candidates[i].End.Current > Candidates[i + 1].Start.Current)
                    stats.Overlaps++;
            }

            return stats;
        }
    }
}
