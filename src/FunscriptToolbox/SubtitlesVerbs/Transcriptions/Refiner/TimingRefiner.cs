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

        // A mountain whose peak is below this ratio of the loudest level inside the original subtitle is noise.
        private double SignificantMountainRatio { get; set; } = 0.15;

        /// <summary>
        /// When a boundary lands in a mountain, look ahead this far outward.
        /// If significant energy/peak is found, snap directly to that island's edge.
        /// </summary>
        private double IslandPeakLookaheadSeconds { get; set; } = 0.10;

        private double MaxSimilarityDrop { get; set; } = 0.3;

        public AudioTerrainMapping TerrainMapping { get; set; }

        public List<TimingCandidate> Candidates { get; set; } = new List<TimingCandidate>();

        public TimingRefiner(PcmAudio fullPcmAudioForRefiner)
        {
            TerrainMapping = new AudioTerrainMapping(fullPcmAudioForRefiner)
            {
                /// <summary>Terrain must be above (floor * this) to be suspected speech. Lower = more sensitive.</summary>
                SpeechRatio = 4f,
                /// <summary>A suspected-speech zone must reach (floor * this) somewhere, otherwise it is just noise.</summary>
                PeakRatio = 6f,
                /// <summary>A valley is a cut if it comes within this fraction of the way from the floor to the lower neighbouring peak. Lower = fewer cuts.</summary>
                ValleyReach = 0.25f,
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
                SignificantLevel = (float)(SignificantMountainRatio * (TerrainMapping?.GetPeakLevel(originalItem.StartTime, originalItem.EndTime) ?? 0f))
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
        /// - If in silence/insignificant: jump inward to speech edge.
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
            var lookahead = TimeSpan.FromSeconds(IslandPeakLookaheadSeconds);

            // 1. Silent or insignificant: snap inward towards speech edge
            if (TerrainMapping.IsInsignificant(t, cand.SignificantLevel))
            {
                var edge = TerrainMapping.FindDip(t, side, MoveDirection.Inward, new DipSearch
                {
                    MinJump = TimeSpan.MaxValue,
                    StopAtSpeechEdge = true,
                    SignificantLevel = 0,
                    Inclusive = true,
                    Limit = other
                });
                if (edge.HasValue)
                    return edge.Value;

                if (TerrainMapping.IsSilent(t))
                {
                    var dip = TerrainMapping.FindDip(t, side, MoveDirection.Inward, new DipSearch { Inclusive = true, Limit = other });
                    if (dip.HasValue && (dip.Value - t).Duration() <= maxDrift)
                        return dip.Value;
                }
                return t;
            }

            // 2. Inside a mountain: check outward island lookahead
            var lookFrom = isStart ? (t > lookahead ? t - lookahead : TimeSpan.Zero) : t;
            var lookTo = isStart ? t : t + lookahead;
            float lookaheadPeak = TerrainMapping.GetPeakLevel(lookFrom, lookTo);

            // If there is significant energy/peak in the outward direction, capture whole island
            if (lookaheadPeak >= cand.SignificantLevel)
            {
                var islandEdge = TerrainMapping.FindDip(t, side, MoveDirection.Outward, new DipSearch
                {
                    StopAtSpeechEdge = true,
                    SignificantLevel = cand.SignificantLevel,
                    Inclusive = true,
                    Limit = isStart ? t - maxDrift : t + maxDrift
                });

                if (islandEdge.HasValue && (islandEdge.Value - t).Duration() <= maxDrift)
                    return islandEdge.Value;
            }

            // Regular outward snap to nearest dip
            var snapped = TerrainMapping.FindDip(t, side, MoveDirection.Outward, new DipSearch { Inclusive = true });
            return snapped.HasValue && (snapped.Value - t).Duration() <= maxDrift
                ? snapped.Value
                : t;
        }

        public void EnforceMinimumDuration(TimingCandidate cand, ref TimeSpan start, ref TimeSpan end)
        {
            var minDuration = this.MinSubtitleDuration;
            var maxDrift = this.MaxDrift;

            if (TerrainMapping != null)
            {
                for (int i = 0; i < 20 && end - start < minDuration; i++)
                {
                    var endDip = TerrainMapping.FindDip(end, BoundarySide.End, MoveDirection.Outward, new DipSearch { Limit = cand.OriginalEndTime + maxDrift });
                    if (endDip.HasValue)
                        end = endDip.Value;
                    if (end - start >= minDuration)
                        break;

                    var startDip = TerrainMapping.FindDip(start, BoundarySide.Start, MoveDirection.Outward, new DipSearch { Limit = cand.OriginalStartTime - maxDrift });
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

            CoordinateNeighborCandidates(this.Candidates);
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

            if (cand.Plan == null)
            {
                cand.LockAll();
                SetFinalNotes(cand);
                if (!string.IsNullOrEmpty(stopReason))
                {
                    cand.AddTraceRow("Stop", ' ', cand.CurrentTestStart, cand.CurrentTestEnd, ' ', stopReason);
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
            var s = cand.Side(side);
            if (s.IsLocked)
                return;

            var boundary = test.Boundary(side);

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
                SignificantLevel = cand.SignificantLevel,
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
            var fallback = cand.Tests
                .OrderByDescending(t => t.Match.CoversAll)
                .ThenByDescending(t => t.Match.Similarity)
                .ThenByDescending(t => t.Duration)
                .First();

            var start = cand.Start.Good ?? fallback.Start;
            var end = cand.End.Good ?? fallback.End;

            if (end <= start)
            {
                start = fallback.Start;
                end = fallback.End;
            }

            if (start != cand.CurrentTestStart || end != cand.CurrentTestEnd)
            {
                cand.AddTraceRow(
                    "Best",
                    TimingCandidate.GetStartMod(cand.CurrentTestStart, start),
                    start,
                    end,
                    TimingCandidate.GetEndMod(cand.CurrentTestEnd, end));
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

        public void CoordinateNeighborCandidates(List<TimingCandidate> candidates)
        {
            var sorted = candidates.OrderBy(c => c.OriginalStartTime).ToList();
            var minDuration = this.MinSubtitleDuration;

            for (int i = 0; i < sorted.Count - 1; i++)
            {
                var cur = sorted[i];
                var next = sorted[i + 1];

                // Case A: Next (Right) confirmed Start with Ok -> creates HardLimitEnd on Cur (Left)
                if (next.Start.IsLocked && next.Start.Good.HasValue && next.Start.GoodState == SideState.Ok)
                {
                    var hardLimit = next.Start.Good.Value;
                    cur.HardLimitEnd = hardLimit;

                    var curTargetEnd = cur.Plan?.End ?? cur.CurrentTestEnd;
                    if (curTargetEnd > hardLimit)
                    {
                        // Look for a terrain dip at or before hardLimit, but never before cur.CurrentTestStart
                        var clampedEnd = FindTerrainDipAtOrBefore(hardLimit, cur.CurrentTestStart);
                        if (clampedEnd <= cur.CurrentTestStart)
                            clampedEnd = hardLimit;

                        var targetStart = cur.CurrentTestStart;

                        // If clamped duration is less than minDuration, attempt to expand Start earlier to compensate
                        if (clampedEnd - targetStart < minDuration)
                        {
                            var maxDrift = this.MaxDrift;
                            var startLimit = cur.HardLimitStart ?? (cur.OriginalStartTime - maxDrift);
                            var neededStart = clampedEnd - minDuration;
                            if (neededStart < startLimit)
                                neededStart = startLimit;

                            var expandedStart = FindTerrainDipAtOrBefore(neededStart, startLimit);
                            if (expandedStart < targetStart)
                                targetStart = expandedStart;

                            // Absolute fallback floor: allow down to 0.1s
                            if (clampedEnd - targetStart < TimeSpan.FromSeconds(0.1) && clampedEnd > startLimit)
                            {
                                var minFloor = clampedEnd - TimeSpan.FromSeconds(0.1);
                                targetStart = minFloor > startLimit ? minFloor : startLimit;
                            }
                        }

                        // Always unlock and create the plan for cur
                        cur.End.IsLocked = false;
                        cur.End.Good = null;
                        cur.End.GoodState = null;

                        char startMod = TimingCandidate.GetStartMod(cur.CurrentTestStart, targetStart);
                        cur.CurrentTestStart = targetStart;
                        // Guard against zero or inverted duration: never create a zero-length plan
                        if (clampedEnd <= targetStart + TimeSpan.FromSeconds(0.05))
                            continue;

                        cur.CurrentTestEnd = clampedEnd;
                        cur.Plan = new TimeWindow(targetStart, clampedEnd, TimingMove.Shrink, cur.Tests.Count - 1);
                        cur.AddTraceRow("Shrk", startMod, targetStart, clampedEnd, '-', $"Hard limit {hardLimit:hh\\:mm\\:ss\\.fff}");
                    }
                }

                // Case B: Cur (Left) confirmed End with Ok -> creates HardLimitStart on Next (Right)
                if (cur.End.IsLocked && cur.End.Good.HasValue && cur.End.GoodState == SideState.Ok)
                {
                    var hardLimit = cur.End.Good.Value;
                    next.HardLimitStart = hardLimit;

                    var nextTargetStart = next.Plan?.Start ?? next.CurrentTestStart;
                    if (nextTargetStart < hardLimit)
                    {
                        // Look for a terrain dip at or after hardLimit, but never after next.CurrentTestEnd
                        var clampedStart = FindTerrainDipAtOrAfter(hardLimit, next.CurrentTestEnd);
                        if (clampedStart >= next.CurrentTestEnd)
                            clampedStart = hardLimit;

                        var targetEnd = next.CurrentTestEnd;

                        // If clamped duration is less than minDuration, attempt to expand End later to compensate
                        if (targetEnd - clampedStart < minDuration)
                        {
                            var maxDrift = this.MaxDrift;
                            var endLimit = next.HardLimitEnd ?? (next.OriginalEndTime + maxDrift);
                            var neededEnd = clampedStart + minDuration;
                            if (neededEnd > endLimit)
                                neededEnd = endLimit;

                            var expandedEnd = FindTerrainDipAtOrAfter(neededEnd, endLimit);
                            if (expandedEnd > targetEnd)
                                targetEnd = expandedEnd;

                            // Absolute fallback floor: allow down to 0.1s
                            if (targetEnd - clampedStart < TimeSpan.FromSeconds(0.1) && clampedStart < endLimit)
                            {
                                var minCeil = clampedStart + TimeSpan.FromSeconds(0.1);
                                targetEnd = minCeil < endLimit ? minCeil : endLimit;
                            }
                        }

                        // Always unlock and create the plan for next
                        next.Start.IsLocked = false;
                        next.Start.Good = null;
                        next.Start.GoodState = null;

                        char endMod = TimingCandidate.GetEndMod(next.CurrentTestEnd, targetEnd);
                        next.CurrentTestStart = clampedStart;
                        // Guard against zero or inverted duration: never create a zero-length plan
                        if (targetEnd <= clampedStart + TimeSpan.FromSeconds(0.05))
                            continue;

                        next.CurrentTestEnd = targetEnd;
                        next.Plan = new TimeWindow(clampedStart, targetEnd, TimingMove.Shrink, next.Tests.Count - 1);
                        next.AddTraceRow("Shrk", '+', clampedStart, targetEnd, endMod, $"Hard limit {hardLimit:hh\\:mm\\:ss\\.fff}");
                    }
                }
            }
        }

        public void FinalizeUndecidedOverlaps(List<TimingCandidate> candidates)
        {
            var sorted = candidates
                .OrderBy(c => c.CurrentTestStart)
                .ToList();

            for (int i = 0; i < sorted.Count - 1; i++)
            {
                var cur = sorted[i];
                var next = sorted[i + 1];

                var curEnd = cur.CurrentTestEnd;
                var nextStart = next.CurrentTestStart;

                if (curEnd > nextStart)
                {
                    char curEndMod = TimingCandidate.GetEndMod(curEnd, cur.OriginalEndTime);
                    char nextStartMod = TimingCandidate.GetStartMod(nextStart, next.OriginalStartTime);

                    cur.CurrentTestEnd = cur.OriginalEndTime;
                    next.CurrentTestStart = next.OriginalStartTime;

                    cur.AddValidationNote("Review: Shared Audio Zone (Reverted to Original)");
                    next.AddValidationNote("Review: Shared Audio Zone (Reverted to Original)");

                    cur.AddTraceRow("Rev ", ' ', cur.CurrentTestStart, cur.CurrentTestEnd, curEndMod, "Revert to Original (Shared Zone)");
                    next.AddTraceRow("Rev ", nextStartMod, next.CurrentTestStart, next.CurrentTestEnd, ' ', "Revert to Original (Shared Zone)");
                }
            }
        }

        public void MediateOverlappingCandidates(List<TimingCandidate> candidates)
        {
            FinalizeUndecidedOverlaps(candidates);
        }
    }
}
