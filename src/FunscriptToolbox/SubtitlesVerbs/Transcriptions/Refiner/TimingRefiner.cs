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
        public double MaxDriftSeconds { get; set; } = 0.6;

        public double MinSubtitleDurationSeconds { get; set; } = 0.5;

        public double SimilarityTieTolerance { get; set; } = 0.03;

        // A side with N missing syllables and at least N extra ones is read as a substitution (side covered)
        // only when N is at most this value.
        public int MaxSubstitutedSyllables { get; set; } = 2;

        // A boundary move must be at least this long, unless it stops exactly on a speech edge.
        public double MinJumpSeconds { get; set; } = 0.07;

        // A mountain whose peak is below this ratio of the loudest level inside the original subtitle is noise.
        public double SignificantMountainRatio { get; set; } = 0.15;

        /// <summary>
        /// When a boundary lands in a mountain, look ahead this far outward.
        /// If significant energy/peak is found, snap directly to that island's edge.
        /// </summary>
        public double IslandPeakLookaheadSeconds { get; set; } = 0.10;

        public double MaxSimilarityDrop { get; set; } = 0.3;

        public AudioTerrainMapping TerrainMapping { get; set; }

        public List<TimingCandidate> Candidates { get; set; } = new List<TimingCandidate>();

        public TimingRefiner() { }

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
            var maxDrift = TimeSpan.FromSeconds(MaxDriftSeconds);
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
            var minDuration = TimeSpan.FromSeconds(MinSubtitleDurationSeconds);
            var maxDrift = TimeSpan.FromSeconds(MaxDriftSeconds);

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

            MediateOverlappingCandidates(this.Candidates);
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
            if (!cand.IsFullyLocked && cand.RetryCount < MaxRetriesPerNode)
                PlanNextWindow(cand, test);

            if (cand.Plan == null)
            {
                cand.LockAll();
                SetFinalNotes(cand);
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
                s.IsLocked = true;
            }
        }

        public void PlanNextWindow(TimingCandidate cand, TestedWindow test)
        {
            var nextStart = PlanSide(cand, test, BoundarySide.Start);
            var nextEnd = PlanSide(cand, test, BoundarySide.End);

            if (cand.IsFullyLocked || nextEnd <= nextStart)
                return;

            if (cand.FindTest(nextStart, nextEnd) != null)
                return;

            cand.Plan = new TimeWindow(nextStart, nextEnd, TimingMove.Expand, cand.Tests.Count - 1);
        }

        public TimeSpan PlanSide(TimingCandidate cand, TestedWindow test, BoundarySide side)
        {
            var s = cand.Side(side);
            if (s.IsLocked)
                return s.Good ?? test.Boundary(side);

            var current = test.Boundary(side);
            var maxDrift = TimeSpan.FromSeconds(MaxDriftSeconds);
            var limit = side == BoundarySide.Start ? cand.OriginalStartTime - maxDrift : cand.OriginalEndTime + maxDrift;

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
                    ? "Unsure: Audio Inaudible"
                    : $"Unsure: Text Mismatch: {best.HeardText}");
                return;
            }

            if (cand.Start.Good == null)
                cand.AddValidationNote("Unsure: Missing Start (drift limit reached)");
            if (cand.End.Good == null)
                cand.AddValidationNote("Unsure: Missing End (drift limit reached)");
        }

        public void MediateOverlappingCandidates(List<TimingCandidate> candidates)
        {
            var sorted = candidates
                .OrderBy(c => c.CurrentTestStart)
                .ToList();

            var minDuration = TimeSpan.FromSeconds(MinSubtitleDurationSeconds);

            for (int i = 0; i < sorted.Count - 1; i++)
            {
                var cur = sorted[i];
                var next = sorted[i + 1];

                var curStart = cur.CurrentTestStart;
                var curEnd = cur.CurrentTestEnd;
                var nextStart = next.CurrentTestStart;
                var nextEnd = next.CurrentTestEnd;

                if (curEnd > nextStart)
                {
                    var overlapDuration = curEnd - nextStart;
                    var midTicks = (curEnd.Ticks + nextStart.Ticks) / 2;
                    var midpoint = TimeSpan.FromTicks(midTicks);

                    if (midpoint - curStart < minDuration)
                        midpoint = curStart + minDuration;
                    else if (nextEnd - midpoint < minDuration)
                        midpoint = nextEnd - minDuration;

                    if (midpoint <= curStart || midpoint >= nextEnd)
                        midpoint = TimeSpan.FromTicks(midTicks);

                    char curEndMod = TimingCandidate.GetEndMod(curEnd, midpoint);
                    char nextStartMod = TimingCandidate.GetStartMod(nextStart, midpoint);

                    cur.CurrentTestEnd = midpoint;
                    next.CurrentTestStart = midpoint;

                    if (overlapDuration.TotalMilliseconds >= 50)
                    {
                        cur.AddValidationNote("Review: Shared Audio Zone");
                        next.AddValidationNote("Review: Shared Audio Zone");

                        cur.AddTraceRow("Med", ' ', cur.CurrentTestStart, cur.CurrentTestEnd, curEndMod);
                        next.AddTraceRow("Med", nextStartMod, next.CurrentTestStart, next.CurrentTestEnd, ' ');
                    }
                }
            }
        }
    }
}
