using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{

    /// <summary>
    /// Refines the timing of transcription candidates using audio terrain mapping and AI transcription feedback.
    /// </summary>
    public class TimingRefiner
    {
        // Maximum number of rounds per candidate (a round tests 1 window).
        public int MaxRetriesPerNode { get; set; } = 8;

        // How far (seconds) a boundary may move OUTWARD from its original position.
        public TimeSpan MaxDrift { get; set; }

        public TimeSpan MinSubtitleDuration { get; set; }

        // A side with N missing syllables and at least N extra ones is read as a substitution (side covered)
        // only when N is at most this value.
        private int MaxSubstitutedSyllables { get; set; } = 2;

        // A boundary move must be at least this long, unless it stops exactly on a speech edge.
        private double MinJumpSeconds { get; set; } = 0.07;

        private double MaxSimilarityDrop { get; set; } = 0.3;

        public AudioTerrainMapping TerrainMapping { get; set; }

        public List<TimingCandidate> Candidates { get; set; } = new List<TimingCandidate>();

        public TimingRefiner(PcmAudio fullPcmAudioForRefiner, int maxRetriesPerNode, TimeSpan maxDrift, TimeSpan minSubtitleDuration, float valleyReach)
        {
            this.MaxRetriesPerNode = maxRetriesPerNode;
            this.MaxDrift = maxDrift;
            this.MinSubtitleDuration = minSubtitleDuration;
            TerrainMapping = new AudioTerrainMapping(fullPcmAudioForRefiner)
            {
                /// <summary>Terrain must be above (floor * this) to be suspected speech. Lower = more sensitive.</summary>
                SpeechRatio = 4f,
                /// <summary>A suspected-speech zone must reach (floor * this) somewhere, otherwise it is just noise.</summary>
                PeakRatio = 6f,
                /// <summary>A valley is a cut if it comes within this fraction of the way from the floor to the lower neighbouring peak. Lower = fewer cuts.</summary>
                ValleyReach = valleyReach,
            };
            TerrainMapping.Compute();
        }

        public void AddCandidate(TimedItemWithMetadata originalItem, string originalText)
        {
            var candidate = new TimingCandidate
            {
                OriginalStartTime = originalItem.StartTime,
                OriginalEndTime = originalItem.EndTime,
                OriginalItem = originalItem,
                OriginalText = originalText,
                CurrentTestStart = originalItem.StartTime,
                CurrentTestEnd = originalItem.EndTime,
            };
            TimeSpan start = SnapInitialBoundary(candidate, BoundarySide.Start);
            TimeSpan end = SnapInitialBoundary(candidate, BoundarySide.End);

            // Both boundaries ended up crossing each other: keep the original timing
            if (end <= start)
            {
                start = originalItem.StartTime;
                end = originalItem.EndTime;
            }

            EnforceMinimumDuration(candidate, ref start, ref end);

            candidate.CurrentTestStart = start;
            candidate.CurrentTestEnd = end;
            candidate.Plan = new TimeWindow(start, end, TimingMove.Init);

            candidate.AddTraceRow("Orig", ' ', originalItem.StartTime, originalItem.EndTime, ' ', "", candidate.OriginalText);
            this.Candidates.Add(candidate);
        }

        /// <summary>
        /// Snaps initial boundaries prioritizing grabbing whole islands:
        /// - If in silence: jump inward to speech edge.
        /// - If inside a mountain: check outward lookahead window for energy/peaks; if present, snap
        ///   to the full outer boundary of that mountain island.
        /// </summary>
        public TimeSpan SnapInitialBoundary(TimingCandidate cand, BoundarySide side)
        {
            if (TerrainMapping == null)
                return side == BoundarySide.Start ? cand.OriginalStartTime : cand.OriginalEndTime;

            bool isStart = side == BoundarySide.Start;
            var t = isStart ? cand.OriginalStartTime : cand.OriginalEndTime;
            var other = isStart ? cand.OriginalEndTime : cand.OriginalStartTime;
            var maxDrift = this.MaxDrift;

            // 1. Silent: snap inward towards speech edge (clamped to MaxDrift and preserving MinSubtitleDuration)
            if (TerrainMapping.IsSilent(t))
            {
                var inwardLimit = isStart
                    ? t + maxDrift
                    : t - maxDrift;

                if (isStart && other - t > this.MinSubtitleDuration)
                {
                    var maxStart = other - this.MinSubtitleDuration;
                    if (inwardLimit > maxStart)
                        inwardLimit = maxStart;
                }
                else if (!isStart && t - other > this.MinSubtitleDuration)
                {
                    var minEnd = other + this.MinSubtitleDuration;
                    if (inwardLimit < minEnd)
                        inwardLimit = minEnd;
                }

                var edge = TerrainMapping.FindDip(t, side, MoveDirection.Inward, new DipSearch
                {
                    MinJump = TimeSpan.MaxValue,
                    StopAtSpeechEdge = true,
                    Inclusive = true,
                    Limit = inwardLimit
                });
                if (edge.HasValue && (edge.Value - t).Duration() <= maxDrift)
                    return edge.Value;

                var dip = TerrainMapping.FindDip(t, side, MoveDirection.Inward, new DipSearch { Inclusive = true, Limit = inwardLimit });
                if (dip.HasValue && (dip.Value - t).Duration() <= maxDrift)
                    return dip.Value;
                return t;
            }

            // 2. Inside a mountain: snap outward to speech edge
            var islandEdge = TerrainMapping.FindDip(t, side, MoveDirection.Outward, new DipSearch
            {
                StopAtSpeechEdge = true,
                Inclusive = true,
                Limit = isStart ? t - maxDrift : t + maxDrift
            });

            if (islandEdge.HasValue && (islandEdge.Value - t).Duration() <= maxDrift)
                return islandEdge.Value;

            // Regular outward snap to nearest dip
            var snapped = TerrainMapping.FindDip(t, side, MoveDirection.Outward, new DipSearch { Inclusive = true });
            return snapped.HasValue && (snapped.Value - t).Duration() <= maxDrift
                ? snapped.Value
                : t;
        }

        private bool AreConsecutiveTextsSimilar(TimingCandidate cur, TimingCandidate next)
        {
            if (string.IsNullOrWhiteSpace(cur.OriginalText) || string.IsNullOrWhiteSpace(next.OriginalText))
                return false;

            var match = RomajiSyllableMatcher.Analyze(cur.OriginalText, next.OriginalText, MaxSubstitutedSyllables);
            return !match.IsMismatch && match.CoversAll && match.Similarity >= 0.75;
        }

        public void EnforceMinimumDuration(TimingCandidate cand, ref TimeSpan start, ref TimeSpan end)
        {
            var minDuration = this.MinSubtitleDuration;
            var maxDrift = this.MaxDrift;

            // De-duplicate Init.Start: two consecutive candidates should not start their Init test on the exact same mountain dip
            var prev = this.Candidates.LastOrDefault();
            if (prev != null && start <= prev.CurrentTestStart)
            {
                var nextDip = TerrainMapping?.FindDip(
                    prev.CurrentTestStart,
                    BoundarySide.Start,
                    MoveDirection.Inward,
                    new DipSearch
                    {
                        Inclusive = false,
                        Limit = cand.OriginalStartTime + maxDrift
                    });

                if (nextDip.HasValue && (nextDip.Value - cand.OriginalStartTime).Duration() <= maxDrift)
                {
                    start = nextDip.Value;
                }
                else if (cand.OriginalStartTime > prev.CurrentTestStart)
                {
                    start = cand.OriginalStartTime;
                }

                if (end <= start)
                {
                    end = cand.OriginalEndTime > start ? cand.OriginalEndTime : start + minDuration;
                }
            }

            var startMinLimit = cand.OriginalStartTime - maxDrift;
            if (prev != null && startMinLimit <= prev.CurrentTestStart)
            {
                startMinLimit = prev.CurrentTestStart + TimeSpan.FromMilliseconds(1);
            }

            if (TerrainMapping != null)
            {
                for (int i = 0; i < 20 && end - start < minDuration; i++)
                {
                    var endDip = TerrainMapping.FindDip(end, BoundarySide.End, MoveDirection.Outward, new DipSearch { Limit = cand.OriginalEndTime + maxDrift });
                    if (endDip.HasValue)
                        end = endDip.Value;
                    if (end - start >= minDuration)
                        break;

                    var startDip = TerrainMapping.FindDip(start, BoundarySide.Start, MoveDirection.Outward, new DipSearch { Limit = startMinLimit });
                    if (startDip.HasValue)
                        start = startDip.Value;

                    if (!endDip.HasValue && !startDip.HasValue)
                        break;
                }
            }

            if (end - start < minDuration)
                end = start + minDuration;
        }

        public void ProcessBatchResults(IEnumerable<TranscribedItem> heardItems, string heardMetadataKey)
        {
            var touched = new List<TimingCandidate>();

            foreach (var fakeItem in heardItems)
            {
                var cand = this.Candidates.FirstOrDefault(c => c.Plan != null && c.Plan.Start == fakeItem.StartTime && c.Plan.End == fakeItem.EndTime);
                if (cand == null) continue;

                string heard = fakeItem.Metadata.TryGetValue(heardMetadataKey, out var h) ? h : string.Empty;

                var plan = cand.Plan;
                var parent = plan.ParentIndex >= 0 && plan.ParentIndex < cand.Tests.Count ? cand.Tests[plan.ParentIndex] : null;

                var test = new TestedWindow
                {
                    Start = plan.Start,
                    End = plan.End,
                    Kind = plan.Kind,
                    ParentIndex = plan.ParentIndex,
                    Round = cand.RetryCount,
                    HeardText = heard,
                    Match = RomajiSyllableMatcher.Analyze(cand.OriginalText, heard, MaxSubstitutedSyllables)
                };

                char startMod = TimingCandidate.GetStartMod(parent?.Start ?? cand.OriginalStartTime, test.Start);
                char endMod = TimingCandidate.GetEndMod(parent?.End ?? cand.OriginalEndTime, test.End);

                cand.Tests.Add(test);
                cand.Plan = null;

                string stepName = cand.Tests.Count == 1 ? "Init" : $"#{cand.Tests.Count - 1}";
                string result = test.Match.ToTraceString();
                cand.AddTraceRow(stepName, startMod, test.Start, test.End, endMod, result, heard);

                if (!touched.Contains(cand))
                    touched.Add(cand);
            }

            foreach (var cand in touched)
            {
                AdvanceCandidate(cand);
            }

            if (this.Candidates.Any(f => !f.Start.IsLocked))
            {
                CoordinateNeighborCandidates(this.Candidates);
            }
            if (this.Candidates.All(f => f.Start.IsLocked))
            {
                CoordinateNeighborCandidates(this.Candidates);
            }
            FinalizeCandidateLocks(this.Candidates);
        }

        public void AdvanceCandidate(TimingCandidate cand)
        {
            var test = cand.Tests.Last();
            cand.RetryCount++;

            double bestSimilarity = cand.Tests
                .Select(t => t.Match.Similarity)
                .DefaultIfEmpty(0)
                .Max();
            bool degraded = test.Match.Similarity < bestSimilarity - MaxSimilarityDrop;

            JudgeSide(cand, test, BoundarySide.Start, degraded);
            JudgeSide(cand, test, BoundarySide.End, degraded);

            UpdateCurrentBest(cand);

            cand.Plan = null;
            string stopReason = null;
            if (cand.RetryCount >= MaxRetriesPerNode)
            {
                stopReason = $"Max retries reached ({cand.RetryCount}/{MaxRetriesPerNode})";
            }
            else if (cand.IsFullyLocked)
            {
                stopReason = "Both sides covered";
            }
            else
            {
                stopReason = PlanNextWindow(cand, test);
            }

            cand.PendingStopReason = stopReason;
        }

        public void FinalizeCandidateLocks(List<TimingCandidate> candidates)
        {
            foreach (var cand in candidates)
            {
                if (cand.Plan != null)
                {
                    cand.HasStopped = false;
                    continue;
                }

                if (!cand.IsFullyLocked || !cand.HasStopped)
                {
                    cand.LockAll();
                    SetFinalNotes(cand);
                }

                if (!cand.HasStopped)
                {
                    cand.HasStopped = true;
                    var reason = !string.IsNullOrEmpty(cand.PendingStopReason)
                        ? cand.PendingStopReason
                        : "Both sides covered";
                    cand.AddTraceRow("Stop", ' ', cand.CurrentTestStart, cand.CurrentTestEnd, ' ', reason);
                }
            }
        }

        /// <summary>
        /// Pure expansion judgement:
        /// - If covers (Ok / Extra / Substituted): Lock immediately.
        /// - If Missing / Mismatch: Keep unlocked to expand further outward.
        /// </summary>
        public void JudgeSide(TimingCandidate cand, TestedWindow test, BoundarySide side, bool degraded)
        {
            var boundary = test.Boundary(side);

            if (!test.Match.IsMismatch && !degraded && test.Match.Covers(side))
            {
                if (side == BoundarySide.Start)
                    cand.LastCompleteStart = boundary;
            }

            var s = cand.Side(side);
            if (s.IsLocked)
                return;

            if (!test.Match.IsMismatch && !degraded && test.Match.Covers(side))
            {
                s.Good = boundary;
                s.GoodState = test.Match.State(side);
                s.IsLocked = true;
            }
        }

        public string PlanNextWindow(TimingCandidate cand, TestedWindow test)
        {
            bool startWasLocked = cand.Start.IsLocked;
            bool endWasLocked = cand.End.IsLocked;

            var nextStart = PlanSide(cand, test, BoundarySide.Start);
            var nextEnd = PlanSide(cand, test, BoundarySide.End);

            var maxDrift = this.MaxDrift;
            var startLimit = cand.HardLimitStart ?? (cand.OriginalStartTime - maxDrift);
            var endLimit = cand.HardLimitEnd ?? (cand.OriginalEndTime + maxDrift);

            bool startHitLimit = !startWasLocked && cand.Start.IsLocked && cand.Start.Good == null;
            bool endHitLimit = !endWasLocked && cand.End.IsLocked && cand.End.Good == null;

            if (startHitLimit && endHitLimit)
                return $"Start & End drift limit reached (limits: {startLimit:hh\\:mm\\:ss\\.fff}, {endLimit:hh\\:mm\\:ss\\.fff})";
            if (startHitLimit)
                return $"Start drift limit reached (limit: {startLimit:hh\\:mm\\:ss\\.fff})";
            if (endHitLimit)
                return $"End drift limit reached (limit: {endLimit:hh\\:mm\\:ss\\.fff})";

            if (cand.IsFullyLocked)
                return "Both sides covered";

            if (nextEnd <= nextStart)
                return $"Boundaries crossed ({nextStart:hh\\:mm\\:ss\\.fff} >= {nextEnd:hh\\:mm\\:ss\\.fff})";

            if (cand.FindTest(nextStart, nextEnd) != null)
                return $"Window already tested ({nextStart:hh\\:mm\\:ss\\.fff}=>{nextEnd:hh\\:mm\\:ss\\.fff})";

            cand.Plan = new TimeWindow(nextStart, nextEnd, TimingMove.Expand, cand.Tests.Count - 1);
            return null;
        }

        public TimeSpan PlanSide(TimingCandidate cand, TestedWindow test, BoundarySide side)
        {
            var s = cand.Side(side);
            if (s.IsLocked)
                return s.Good ?? test.Boundary(side);

            var current = test.Boundary(side);
            var maxDrift = this.MaxDrift;
            var limit = side == BoundarySide.Start ? cand.OriginalStartTime - maxDrift : cand.OriginalEndTime + maxDrift;

            // Clamp limit by hard limits from neighbors if present
            if (side == BoundarySide.Start && cand.HardLimitStart.HasValue && limit < cand.HardLimitStart.Value)
            {
                limit = cand.HardLimitStart.Value;
            }
            else if (side == BoundarySide.End && cand.HardLimitEnd.HasValue && limit > cand.HardLimitEnd.Value)
            {
                limit = cand.HardLimitEnd.Value;
            }

            if (TerrainMapping == null)
            {
                s.IsLocked = true;
                return s.Good ?? current;
            }

            // Only move OUTWARD
            var next = TerrainMapping.FindDip(current, side, MoveDirection.Outward, new DipSearch
            {
                MinJump = TimeSpan.FromSeconds(MinJumpSeconds),
                StopAtSpeechEdge = true,
                Limit = limit
            });

            if (!next.HasValue)
            {
                s.IsLocked = true;
                return s.Good ?? current;
            }

            return next.Value;
        }

        public void UpdateCurrentBest(TimingCandidate cand)
        {
            var validTests = cand.Tests
                .Where(t => (!cand.HardLimitStart.HasValue || t.Start >= cand.HardLimitStart.Value) &&
                            (!cand.HardLimitEnd.HasValue || t.End <= cand.HardLimitEnd.Value))
                .ToList();

            var pool = validTests.Count > 0 ? validTests : cand.Tests;

            var fallback = pool
                .OrderByDescending(t => t.Match.CoversAll)
                .ThenByDescending(t => t.Match.Similarity)
                .ThenByDescending(t => t.Duration)
                .First();

            var start = cand.Start.Good ?? fallback.Start;
            var end = cand.End.Good ?? fallback.End;

            if (cand.HardLimitStart.HasValue && start < cand.HardLimitStart.Value)
                start = cand.HardLimitStart.Value;
            if (cand.HardLimitEnd.HasValue && end > cand.HardLimitEnd.Value)
                end = cand.HardLimitEnd.Value;

            if (end <= start)
            {
                start = fallback.Start;
                end = fallback.End;
                if (cand.HardLimitEnd.HasValue && end > cand.HardLimitEnd.Value)
                    end = cand.HardLimitEnd.Value;
                if (start >= end && cand.HardLimitStart.HasValue)
                    start = cand.HardLimitStart.Value;
            }

            if (start != cand.CurrentTestStart || end != cand.CurrentTestEnd)
            {
                var bestTest = cand.FindTest(start, end) ?? fallback;
                string result = bestTest?.Match?.ToTraceString();
                string heard = bestTest?.HeardText;

                cand.AddTraceRow(
                    "Best",
                    TimingCandidate.GetStartMod(cand.CurrentTestStart, start),
                    start,
                    end,
                    TimingCandidate.GetEndMod(cand.CurrentTestEnd, end),
                    result,
                    heard);
            }

            cand.CurrentTestStart = start;
            cand.CurrentTestEnd = end;
        }

        public void SetFinalNotes(TimingCandidate cand)
        {
            cand.ValidationNotes.Clear();

            if (cand.Tests.Count > 0 && cand.Tests.All(t => t.Match.IsMismatch))
            {
                var best = cand.Tests.OrderByDescending(t => t.Match.Similarity).First();
                cand.AddValidationNote(string.IsNullOrWhiteSpace(best.HeardText)
                    ? $"Unsure: Audio Inaudible (original): {cand.OriginalText}"
                    : $"Unsure: Text Mismatch (original vs heard): `{cand.OriginalText}` vs `{best.HeardText}` ");
                return;
            }

            if (cand.Start.Good == null)
                cand.AddValidationNote("Unsure: Missing Start (drift limit reached)");
            if (cand.End.Good == null)
                cand.AddValidationNote("Unsure: Missing End (drift limit reached)");
        }

        public TimeSpan FindTerrainDipAtOrBefore(TimeSpan limit, TimeSpan minLimit)
        {
            if (TerrainMapping?.Segments == null || TerrainMapping.Segments.Count == 0)
                return limit;

            // Find the last non-silence speech segment whose EndTime <= limit
            var seg = TerrainMapping.Segments
                .Where(s => !s.IsSilence && s.EndTime <= limit && s.EndTime >= minLimit)
                .OrderByDescending(s => s.EndTime)
                .FirstOrDefault();

            if (seg != null)
                return seg.EndTime;

            return limit;
        }

        public TimeSpan FindTerrainDipAtOrAfter(TimeSpan limit, TimeSpan maxLimit)
        {
            if (TerrainMapping?.Segments == null || TerrainMapping.Segments.Count == 0)
                return limit;

            // Find the first non-silence speech segment whose StartTime >= limit
            var seg = TerrainMapping.Segments
                .Where(s => !s.IsSilence && s.StartTime >= limit && s.StartTime <= maxLimit)
                .OrderBy(s => s.StartTime)
                .FirstOrDefault();

            if (seg != null)
                return seg.StartTime;

            return limit;
        }

        private SideState GetEffectiveSideState(TimingCandidate cand, BoundarySide side)
        {
            var s = cand.Side(side);
            if (s.IsLocked && s.Good.HasValue && (s.GoodState == SideState.Ok || s.GoodState == SideState.Substituted))
                return SideState.Ok;

            var targetBoundary = side == BoundarySide.Start ? cand.CurrentTestStart : cand.CurrentTestEnd;
            var test = cand.Tests.LastOrDefault(t => t.Boundary(side) == targetBoundary && !t.Match.IsMismatch)
                    ?? cand.Tests.LastOrDefault(t => !t.Match.IsMismatch)
                    ?? cand.Tests.LastOrDefault();

            if (test == null || test.Match.IsMismatch)
                return SideState.Missing;

            var state = test.Match.State(side);
            switch (state)
            {
                case SideState.Ok:
                case SideState.Substituted:
                    return SideState.Ok;
                case SideState.Extra:
                    return SideState.Extra;
                default:
                    return SideState.Missing;
            }
        }

        public void CoordinateNeighborCandidates(List<TimingCandidate> candidates)
        {
            var sorted = candidates.OrderBy(c => c.OriginalStartTime).ToList();
            var minDuration = this.MinSubtitleDuration;

            for (int i = 0; i < sorted.Count - 1; i++)
            {
                var cur = sorted[i];
                var next = sorted[i + 1];
                var minStartForCur = i > 0 ? sorted[i - 1].CurrentTestEnd : TimeSpan.Zero;

                var latestNextTest = next.Tests.LastOrDefault();

                // Check Revert on Missing if next was previously pushed right
                if (next.PushedRightByNeighbor && latestNextTest != null)
                {
                    if (latestNextTest.Match.IsMismatch || latestNextTest.Match.StartState == SideState.Missing)
                    {
                        var revertStart = next.LastCompleteStart ?? next.OriginalStartTime;
                        next.PushedRightByNeighbor = false;
                        next.CurrentTestStart = revertStart;
                        next.Start.Good = revertStart;
                        next.Start.GoodState = SideState.Ok;
                        next.Start.IsLocked = true;
                        next.Plan = null;
                        next.AddTraceRow("Rev ", '-', revertStart, next.CurrentTestEnd, ' ', "Revert to LastCompleteStart (over-shrunk into Missing)");

                        cur.HardLimitEnd = revertStart;
                        if ((cur.Plan?.End ?? cur.CurrentTestEnd) > revertStart)
                        {
                            ClampAndLockCurEnd(cur, revertStart, minDuration, minStartForCur, $"Locked to neighbor Start ({revertStart:hh\\:mm\\:ss\\.fff})");
                        }
                        continue;
                    }
                    else if (latestNextTest.Match.StartState == SideState.Ok || latestNextTest.Match.StartState == SideState.Substituted)
                    {
                        // Push right succeeded and reached Ok
                        next.PushedRightByNeighbor = false;
                        next.Start.Good = next.CurrentTestStart;
                        next.Start.GoodState = SideState.Ok;
                        next.Start.IsLocked = true;
                    }
                }

                var curEndState = GetEffectiveSideState(cur, BoundarySide.End);
                var nextStartState = GetEffectiveSideState(next, BoundarySide.Start);

                var curTargetEnd = cur.Plan?.End ?? cur.CurrentTestEnd;
                var nextTargetStart = next.Plan?.Start ?? next.CurrentTestStart;
                bool overlaps = curTargetEnd > nextTargetStart;

                // When consecutive subtitles repeat similar text (or next.Start has reached/crossed cur.Start),
                // RomajiSyllableMatcher greedily matches cur's repetition as next's S=Ok. Treat next.Start as Extra so we push right.
                if (overlaps && nextStartState == SideState.Ok && (AreConsecutiveTextsSimilar(cur, next) || nextTargetStart <= cur.CurrentTestStart))
                {
                    nextStartState = SideState.Extra;
                    if (curEndState == SideState.Extra)
                        curEndState = SideState.Ok;
                }

                // 1. Missing / Extra OR (Ok / Extra when overlapping) => Push Boundary Right 1 mountain
                if ((curEndState == SideState.Missing && nextStartState == SideState.Extra) ||
                    (curEndState == SideState.Ok && nextStartState == SideState.Extra && overlaps))
                {
                    var nextShrunkStart = TerrainMapping?.FindDip(
                        next.CurrentTestStart,
                        BoundarySide.Start,
                        MoveDirection.Inward,
                        new DipSearch
                        {
                            MinJump = TimeSpan.FromSeconds(MinJumpSeconds),
                            StopAtSpeechEdge = true,
                            Limit = next.CurrentTestEnd - minDuration
                        });

                    bool canPushNextRight = nextShrunkStart.HasValue
                        && nextShrunkStart.Value > next.CurrentTestStart
                        && (next.CurrentTestEnd - nextShrunkStart.Value >= TimeSpan.FromSeconds(0.1))
                        && next.FindTest(nextShrunkStart.Value, next.CurrentTestEnd) == null;

                    if (canPushNextRight)
                    {
                        var targetStart = nextShrunkStart.Value;
                        next.PushedRightByNeighbor = true;
                        next.Start.IsLocked = false;
                        next.Start.Good = null;
                        next.Start.GoodState = null;

                        char endMod = TimingCandidate.GetEndMod(next.CurrentTestEnd, next.CurrentTestEnd);
                        next.CurrentTestStart = targetStart;
                        next.Plan = new TimeWindow(targetStart, next.CurrentTestEnd, TimingMove.Shrink, next.Tests.Count - 1);
                        next.AddTraceRow("Shrk", '+', targetStart, next.CurrentTestEnd, endMod, "Coord: Push right 1 mountain (next Extra vs cur)");

                        // Allow cur.End to expand towards targetStart
                        cur.HardLimitEnd = targetStart;
                        if (cur.Plan == null && cur.CurrentTestEnd < targetStart && curEndState != SideState.Ok)
                        {
                            var curExpandedEnd = TerrainMapping?.FindDip(
                                cur.CurrentTestEnd,
                                BoundarySide.End,
                                MoveDirection.Outward,
                                new DipSearch
                                {
                                    MinJump = TimeSpan.FromSeconds(MinJumpSeconds),
                                    StopAtSpeechEdge = true,
                                    Limit = targetStart
                                }) ?? targetStart;

                            if (curExpandedEnd > cur.CurrentTestEnd && cur.FindTest(cur.CurrentTestStart, curExpandedEnd) == null)
                            {
                                cur.End.IsLocked = false;
                                cur.End.Good = null;
                                cur.End.GoodState = null;

                                char startMod = TimingCandidate.GetStartMod(cur.CurrentTestStart, cur.CurrentTestStart);
                                cur.CurrentTestEnd = curExpandedEnd;
                                cur.Plan = new TimeWindow(cur.CurrentTestStart, curExpandedEnd, TimingMove.Expand, cur.Tests.Count - 1);
                                cur.AddTraceRow("Exp ", startMod, cur.CurrentTestStart, curExpandedEnd, '+', $"Coord: Push right (towards {targetStart:hh\\:mm\\:ss\\.fff})");
                            }
                        }
                    }
                    else
                    {
                        // Cannot push next right (no inward dip or inward window was already tested)
                        next.PushedRightByNeighbor = false;
                        next.Start.IsLocked = true;
                        if (next.Start.Good == null)
                            next.Start.Good = next.CurrentTestStart;

                        cur.HardLimitEnd = next.CurrentTestStart;
                        if (overlaps)
                        {
                            ApplyHardLimitEndShrink(cur, next.CurrentTestStart, minDuration, minStartForCur);
                        }
                    }
                    continue;
                }

                // 2. nextStartState is Ok or Extra (covers all remaining Ok/Ok, Extra/Ok, Ok/Extra no-overlap, Extra/Extra, Missing/Ok)
                if (nextStartState == SideState.Ok || nextStartState == SideState.Extra)
                {
                    next.Start.IsLocked = true;
                    if (next.Start.Good == null)
                        next.Start.Good = next.CurrentTestStart;

                    cur.HardLimitEnd = next.CurrentTestStart;
                    if (overlaps)
                    {
                        ApplyHardLimitEndShrink(cur, next.CurrentTestStart, minDuration, minStartForCur);
                    }
                    else if (curEndState != SideState.Missing)
                    {
                        cur.End.IsLocked = true;
                        if (cur.End.Good == null)
                            cur.End.Good = cur.CurrentTestEnd;
                    }
                    continue;
                }

                // 3. nextStartState is Missing (covers Ok/Missing, Extra/Missing, Missing/Missing)
                // next.Start general algorithm expands left. Once next.Start can no longer expand left, lock both at next.Start if overlapping.
                if (nextStartState == SideState.Missing)
                {
                    if (next.Start.IsLocked || next.Plan == null)
                    {
                        if (overlaps)
                        {
                            next.Start.IsLocked = true;
                            if (next.Start.Good == null)
                                next.Start.Good = next.CurrentTestStart;

                            cur.HardLimitEnd = next.CurrentTestStart;
                            ClampAndLockCurEnd(cur, next.CurrentTestStart, minDuration, minStartForCur, $"Locked to neighbor Start ({next.CurrentTestStart:hh\\:mm\\:ss\\.fff})");
                        }
                    }
                    continue;
                }
            }
        }

        private void ApplyHardLimitEndShrink(TimingCandidate cur, TimeSpan hardLimit, TimeSpan minDuration, TimeSpan minStartFloor)
        {
            cur.HardLimitEnd = hardLimit;

            var curTargetEnd = cur.Plan?.End ?? cur.CurrentTestEnd;
            if (curTargetEnd > hardLimit)
            {
                if (cur.LastShrunkHardLimitEnd != hardLimit)
                {
                    var clampedEnd = FindTerrainDipAtOrBefore(hardLimit, cur.CurrentTestStart);
                    if (clampedEnd <= cur.CurrentTestStart)
                        clampedEnd = hardLimit;

                    var targetStart = cur.CurrentTestStart;

                    if (clampedEnd - targetStart < minDuration)
                    {
                        var maxDrift = this.MaxDrift;
                        var startLimit = cur.HardLimitStart ?? (cur.OriginalStartTime - maxDrift);
                        if (startLimit < minStartFloor)
                            startLimit = minStartFloor;

                        var neededStart = clampedEnd - minDuration;
                        if (neededStart < startLimit)
                            neededStart = startLimit;

                        var expandedStart = FindTerrainDipAtOrBefore(neededStart, startLimit);
                        if (expandedStart < targetStart)
                            targetStart = expandedStart;

                        if (clampedEnd - targetStart < TimeSpan.FromSeconds(0.1) && clampedEnd > startLimit)
                        {
                            var minFloor = clampedEnd - TimeSpan.FromSeconds(0.1);
                            targetStart = minFloor > startLimit ? minFloor : startLimit;
                        }
                    }

                    cur.LastShrunkHardLimitEnd = hardLimit;

                    if (clampedEnd > targetStart + TimeSpan.FromSeconds(0.05) &&
                        cur.FindTest(targetStart, clampedEnd) == null)
                    {
                        cur.End.IsLocked = false;
                        cur.End.Good = null;
                        cur.End.GoodState = null;

                        char startMod = TimingCandidate.GetStartMod(cur.CurrentTestStart, targetStart);
                        cur.CurrentTestStart = targetStart;
                        cur.CurrentTestEnd = clampedEnd;
                        cur.Plan = new TimeWindow(targetStart, clampedEnd, TimingMove.Shrink, cur.Tests.Count - 1);
                        cur.AddTraceRow("Shrk", startMod, targetStart, clampedEnd, '-', $"Hard limit {hardLimit:hh\\:mm\\:ss\\.fff}");
                        return;
                    }
                }

                ClampAndLockCurEnd(cur, hardLimit, minDuration, minStartFloor, $"Locked to Hard limit ({hardLimit:hh\\:mm\\:ss\\.fff})");
            }
        }

        private void ClampAndLockCurEnd(TimingCandidate cur, TimeSpan hardLimit, TimeSpan minDuration, TimeSpan minStartFloor, string reason)
        {
            var prevStart = cur.CurrentTestStart;
            if (hardLimit - cur.CurrentTestStart < minDuration)
            {
                var maxDrift = this.MaxDrift;
                var startLimit = cur.HardLimitStart ?? (cur.OriginalStartTime - maxDrift);
                if (startLimit < minStartFloor)
                    startLimit = minStartFloor;

                var neededStart = hardLimit - minDuration;
                if (neededStart < startLimit)
                    neededStart = startLimit;

                var expandedStart = FindTerrainDipAtOrBefore(neededStart, startLimit);
                if (expandedStart < cur.CurrentTestStart && expandedStart >= startLimit)
                {
                    cur.CurrentTestStart = expandedStart;
                }
                else if (neededStart < cur.CurrentTestStart)
                {
                    cur.CurrentTestStart = neededStart;
                }

                if (cur.Start.Good.HasValue && cur.Start.Good.Value > cur.CurrentTestStart)
                {
                    cur.Start.Good = cur.CurrentTestStart;
                }
            }

            char startMod = TimingCandidate.GetStartMod(prevStart, cur.CurrentTestStart);
            cur.CurrentTestEnd = hardLimit;

            var testedAtLimit = cur.FindTest(cur.CurrentTestStart, hardLimit);
            if (testedAtLimit != null && !testedAtLimit.Match.IsMismatch && testedAtLimit.Match.Covers(BoundarySide.End))
            {
                cur.End.Good = hardLimit;
                cur.End.GoodState = testedAtLimit.Match.EndState;
            }
            else
            {
                cur.End.Good = null;
                cur.End.GoodState = null;
            }
            cur.End.IsLocked = true;

            if (cur.Plan != null && !cur.Start.IsLocked && cur.Plan.Start + TimeSpan.FromSeconds(0.05) < hardLimit)
            {
                cur.Plan = new TimeWindow(cur.Plan.Start, hardLimit, cur.Plan.Kind, cur.Plan.ParentIndex);
            }
            else
            {
                cur.Plan = null;
                if (!cur.Start.IsLocked)
                {
                    cur.Start.IsLocked = true;
                    if (cur.Start.Good == null)
                        cur.Start.Good = cur.CurrentTestStart;
                }
                SetFinalNotes(cur);
            }
            cur.AddTraceRow("Lock", startMod, cur.CurrentTestStart, hardLimit, '-', reason);
        }

        public void FinalizeUndecidedOverlaps(List<TimingCandidate> candidates)
        {
            var sorted = candidates
                .OrderBy(c => c.OriginalStartTime)
                .ToList();

            var minViableDuration = TimeSpan.FromSeconds(0.1);

            for (int i = 0; i < sorted.Count - 1; i++)
            {
                var cur = sorted[i];
                var next = sorted[i + 1];

                var curEnd = cur.CurrentTestEnd;
                var nextStart = next.CurrentTestStart;

                bool overlaps = curEnd > nextStart;
                bool curSquashed = (curEnd - cur.CurrentTestStart) < minViableDuration;

                if (overlaps || curSquashed)
                {
                    var prevStart = cur.CurrentTestStart;
                    if (curSquashed)
                    {
                        var minFloor = i > 0 ? sorted[i - 1].CurrentTestEnd : TimeSpan.Zero;
                        if (cur.OriginalStartTime >= minFloor && cur.OriginalStartTime < cur.OriginalEndTime)
                        {
                            cur.CurrentTestStart = cur.OriginalStartTime;
                        }
                    }

                    char curStartMod = TimingCandidate.GetStartMod(prevStart, cur.CurrentTestStart);
                    char curEndMod = TimingCandidate.GetEndMod(curEnd, cur.OriginalEndTime);
                    char nextStartMod = TimingCandidate.GetStartMod(nextStart, next.OriginalStartTime);

                    if (cur.OriginalEndTime > cur.CurrentTestStart)
                        cur.CurrentTestEnd = cur.OriginalEndTime;
                    else
                        cur.CurrentTestEnd = next.OriginalStartTime;

                    next.CurrentTestStart = next.OriginalStartTime;
                    if (next.CurrentTestEnd - next.CurrentTestStart < this.MinSubtitleDuration && next.OriginalEndTime > next.CurrentTestEnd)
                    {
                        next.CurrentTestEnd = next.OriginalEndTime;
                    }

                    cur.AddValidationNote("Review: Shared Audio Zone (Reverted to Original)");
                    next.AddValidationNote("Review: Shared Audio Zone (Reverted to Original)");

                    cur.AddTraceRow("Rev ", curStartMod, cur.CurrentTestStart, cur.CurrentTestEnd, curEndMod, "Revert to Original (Shared Zone)");
                    next.AddTraceRow("Rev ", nextStartMod, next.CurrentTestStart, next.CurrentTestEnd, ' ', "Revert to Original (Shared Zone)");
                }
            }

            foreach (var cand in sorted)
            {
                if ((cand.CurrentTestEnd - cand.CurrentTestStart) < minViableDuration)
                {
                    char startMod = TimingCandidate.GetStartMod(cand.CurrentTestStart, cand.OriginalStartTime);
                    char endMod = TimingCandidate.GetEndMod(cand.CurrentTestEnd, cand.OriginalEndTime);
                    cand.CurrentTestStart = cand.OriginalStartTime;
                    cand.CurrentTestEnd = cand.OriginalEndTime;
                    cand.AddValidationNote("Review: Shared Audio Zone (Reverted to Original)");
                    cand.AddTraceRow("Rev ", startMod, cand.CurrentTestStart, cand.CurrentTestEnd, endMod, "Revert to Original (Squashed Guard)");
                }
            }
        }

        public void MediateOverlappingCandidates(List<TimingCandidate> candidates)
        {
            FinalizeUndecidedOverlaps(candidates);
        }
    }
}
