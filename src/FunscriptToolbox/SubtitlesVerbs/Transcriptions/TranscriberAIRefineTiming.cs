using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    /// <summary>
    /// Refines the timing of an existing transcription by re-transcribing shorter / longer audio clips.
    ///
    /// Clip boundaries only ever move from one "dip" of the audio terrain to another
    /// (see AudioTerrainMappingPhysicBased). Every window that is tested is kept, together with
    /// what the AI heard. Start and end are judged independently: for each side we keep the
    /// tightest boundary known to cover the speech ("Good") and the closest boundary known to
    /// cut it ("Bad"), and bisect between them before locking the side.
    /// </summary>
    public class TranscriberAIRefineTiming : TranscriberAI
    {
        public enum TimingMove
        {
            Init,
            Expand
        }

        public class JobState
        {
            public int Iteration { get; set; }
            public int TotalTestsRun { get; set; }

            public List<TimingCandidate> Candidates { get; set; } = new List<TimingCandidate>();
            public List<TimeSpan> ActiveBatchStartTimes { get; set; }
            public Transcription PendingBatch { get; set; }

            public Dictionary<string, string> CachedResult = new Dictionary<string, string>();

            public void AddCachedResult(TimeSpan startTime, TimeSpan endTime, string result)
            {
                var key = $"{startTime}-{endTime}";
                CachedResult[key] = result;
            }
            public bool TryGetCachedResult(TimeSpan startTime, TimeSpan endTime, out string result)
            {
                var key = $"{startTime}-{endTime}";
                return CachedResult.TryGetValue(key, out result);
            }
        }

        public class TimeWindow
        {
            public TimeSpan Start { get; set; }
            public TimeSpan End { get; set; }
            public TimingMove Kind { get; set; }
            public int ParentIndex { get; set; } = -1;

            [JsonIgnore] public TimeSpan Duration => End - Start;

            public TimeWindow() { }

            public TimeWindow(TimeSpan start, TimeSpan end, TimingMove kind = TimingMove.Init, int parentIndex = -1)
            {
                Start = start;
                End = end;
                Kind = kind;
                ParentIndex = parentIndex;
            }

            public TimeSpan Boundary(BoundarySide side) => side == BoundarySide.Start ? Start : End;
        }

        /// <summary>A window that was sent to the AI, with the answer.</summary>
        public class TestedWindow : TimeWindow
        {
            public int Round { get; set; }
            public string HeardText { get; set; }
            public MatchAnalysis Match { get; set; }
        }

        /// <summary>Search state of one boundary (start or end) of a candidate.</summary>
        public class SideSearch
        {
            // Tightest boundary known to cover the speech on that side.
            public TimeSpan? Good { get; set; }
            public bool IsLocked { get; set; }
        }

        public class TimingCandidate
        {
            public TimeSpan OriginalStartTime { get; set; }
            public TimeSpan OriginalEndTime { get; set; }
            public string OriginalText { get; set; }

            [JsonIgnore] public TimedItemWithMetadata OriginalItem { get; set; }

            // Terrain level under which a mountain is considered noise for this subtitle.
            public float SignificantLevel { get; set; }

            // Best window found so far. Once the candidate is finished, this is the final timing.
            public TimeSpan CurrentTestStart { get; set; }
            public TimeSpan CurrentTestEnd { get; set; }

            // Window waiting to be sent to the AI in the next iteration.
            public TimeWindow Plan { get; set; } = null;

            // Every window already tested, with what the AI heard. Nothing is ever discarded.
            public List<TestedWindow> Tests { get; set; } = new List<TestedWindow>();

            public SideSearch Start { get; set; } = new SideSearch();
            public SideSearch End { get; set; } = new SideSearch();

            public SideSearch Side(BoundarySide side) => side == BoundarySide.Start ? Start : End;
            [JsonIgnore] public bool IsFullyLocked => Start.IsLocked && End.IsLocked;

            public int RetryCount { get; set; }
            public List<string> ValidationNotes { get; set; } = new List<string>();
            public List<string> Trace { get; set; } = new List<string>();

            public TestedWindow FindTest(TimeSpan start, TimeSpan end)
            {
                return Tests.FirstOrDefault(t => t.Start == start && t.End == end);
            }

            public void LockAll()
            {
                Start.IsLocked = true;
                End.IsLocked = true;
            }

            public void AddValidationNote(string note)
            {
                if (!ValidationNotes.Contains(note))
                    ValidationNotes.Add(note);
            }

            public string GetLockTag()
            {
                if (Start.IsLocked && End.IsLocked) return "**";
                if (Start.IsLocked) return "S?";
                if (End.IsLocked) return "?E";
                return "??";
            }

            public static char GetStartMod(TimeSpan prev, TimeSpan cur)
            {
                var diffMs = (cur - prev).TotalMilliseconds;
                if (Math.Abs(diffMs) < 1) return ' ';
                return diffMs > 0 ? '-' : '+';
            }

            public static char GetEndMod(TimeSpan prev, TimeSpan cur)
            {
                var diffMs = (cur - prev).TotalMilliseconds;
                if (Math.Abs(diffMs) < 1) return ' ';
                return diffMs < 0 ? '-' : '+';
            }

            public void AddTraceRow(string step, char startMod, TimeSpan start, TimeSpan end, char endMod, string result = null, string text = null)
            {
                string lockTag = GetLockTag();
                string stepCol = (step ?? "").PadRight(4);
                string boundsCol = $"[{startMod}{start:hh\\:mm\\:ss\\.fff}=>{end:hh\\:mm\\:ss\\.fff}{endMod}]";

                if (string.IsNullOrEmpty(result) && string.IsNullOrEmpty(text))
                {
                    Trace.Add($"{lockTag} {stepCol} {boundsCol}");
                }
                else
                {
                    string resCol = (result ?? "").PadRight(24);
                    string textCol = text != null ? $"'{text}'" : "";
                    Trace.Add($"{lockTag} {stepCol} {boundsCol} {resCol} {textCol}".TrimEnd());
                }
            }
        }

        [JsonProperty(Order = 20)]
        public string SourceAudioIdForWaveform { get; set; }

        [JsonProperty(Order = 34, Required = Required.Always)]
        public AIOptions InnerOptions { get; set; } = new AIOptions();

        // Maximum number of rounds per candidate (a round tests 1 window).
        [JsonProperty(Order = 43)]
        public int MaxRetriesPerNode { get; set; } = 8;

        [JsonProperty(Order = 44)]
        public double MaxBudgetRatio { get; set; } = 8.0;

        // How far (seconds) a boundary may move OUTWARD from its original position.
        [JsonProperty(Order = 45)]
        public double MaxDriftSeconds { get; set; } = 0.6;

        [JsonProperty(Order = 46)]
        public double MinSubtitleDurationSeconds { get; set; } = 0.5;

        [JsonProperty(Order = 47)]
        public double SimilarityTieTolerance { get; set; } = 0.03;

        // A side with N missing syllables and at least N extra ones is read as a substitution (side covered)
        // only when N is at most this value.
        [JsonProperty(Order = 48)]
        public int MaxSubstitutedSyllables { get; set; } = 2;

        // A boundary move must be at least this long, unless it stops exactly on a speech edge.
        [JsonProperty(Order = 49)]
        public double MinJumpSeconds { get; set; } = 0.07;

        // A mountain whose peak is below this ratio of the loudest level inside the original subtitle is noise.
        [JsonProperty(Order = 50)]
        public double SignificantMountainRatio { get; set; } = 0.15;

        /// <summary>
        /// When a boundary lands in a mountain, look ahead this far outward.
        /// If significant energy/peak is found, snap directly to that island's edge.
        /// </summary>
        [JsonProperty(Order = 51)]
        public double IslandPeakLookaheadSeconds { get; set; } = 0.10;

        [JsonProperty(Order = 52)]
        public double MaxSimilarityDrop { get; set; } = 0.3;

        [JsonIgnore]
        private AudioTerrainMapping r_mapping;

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (!string.IsNullOrEmpty(this.SourceAudioIdForWaveform)
                && context.WIP.AudioExtractions.Any(f => f.Id == this.SourceAudioIdForWaveform)
                && context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == this.SourceAudioIdForWaveform && f.IsFinished) == null)
            {
                reason = $"Audio extraction '{this.SourceAudioIdForWaveform}' is not done yet.";
                return false;
            }

            foreach (var extractor in this.InnerOptions.BinaryDataExtractors?.OfType<BinaryDataExtractorAudio>() ?? Array.Empty<BinaryDataExtractorAudio>())
            {
                if (context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == extractor.SourceAudioId && f.IsFinished) == null)
                {
                    reason = $"Audio extraction '{extractor.SourceAudioId}' is not done yet.";
                    return false;
                }
            }
            return base.IsPrerequisitesMet(context, out reason);
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Transcription transcription)
        {
            var jobState = transcription.CurrentJobState as JobState ?? new JobState();
            transcription.CurrentJobState = jobState;

            var audioExtractor = this.InnerOptions.BinaryDataExtractors.OfType<BinaryDataExtractorAudio>().FirstOrDefault();
            var fullPcmAudio = context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == audioExtractor?.SourceAudioId && f.IsFinished)?.PcmAudio;

            if (fullPcmAudio == null)
            {
                context.WriteError("Cannot refine timing: Full PCM audio extraction not found or not finished.");
                return;
            }

            var waveformPcmAudio = (!string.IsNullOrEmpty(this.SourceAudioIdForWaveform)
                ? context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == this.SourceAudioIdForWaveform && f.IsFinished)?.PcmAudio
                : null) ?? fullPcmAudio;

            if (r_mapping == null)
            {
                r_mapping = new AudioTerrainMapping(waveformPcmAudio)
                {
                    /// <summary>Terrain must be above (floor * this) to be suspected speech. Lower = more sensitive.</summary>
                    SpeechRatio = 4f,
                    /// <summary>A suspected-speech zone must reach (floor * this) somewhere, otherwise it is just noise.</summary>
                    PeakRatio = 6f,
                    /// <summary>A valley is a cut if it comes within this fraction of the way from the floor to the lower neighbouring peak. Lower = fewer cuts.</summary>
                    ValleyReach = 0.25f,
                };
                r_mapping.Compute(context);
                context.WriteInfo($"[RefineTiming] Terrain mapping: {r_mapping.Segments.Count(f => !f.IsSilence)} mountains, {r_mapping.Segments.Count(f => f.IsSilence)} silences.");
                DoExportSrt(context, transcription, r_mapping.ToSegmentSubtitles(), "TERRAIN-MAP");
            }
            var aggregation = this.Metadatas?.Aggregate(context);
            var originalItems = (aggregation.ReferenceTimingsWithMetadata ?? Array.Empty<TimedItemWithMetadata>())
                .OrderBy(f => f.StartTime)
                .ToList();

            if (originalItems.Count == 0)
            {
                transcription.MarkAsFinished();
                context.WIP.Save();
                return;
            }

#if DEBUG
            var oldCache = jobState.CachedResult;
            jobState = new JobState();
            jobState.CachedResult = oldCache;
            transcription.CurrentJobState = jobState;
#endif

            if (jobState.Candidates.Count == 0)
            {
                context.WriteInfo($"[RefineTiming] Phase 1: Initializing acoustic bounds on {originalItems.Count} candidates...");
                jobState.Candidates = InitializeCandidates(originalItems);
                DoExportTraceSrt(context, transcription, jobState.Candidates, "FIRST-INIT");
            }
            else
            {
                foreach (var item in originalItems)
                {
                    var cand = jobState.Candidates.FirstOrDefault(c => c.OriginalStartTime == item.StartTime);
                    if (cand != null)
                        cand.OriginalItem = item;
                }
            }

            int totalAllowedTests = Math.Max(50, (int)(originalItems.Count * MaxBudgetRatio));

            while (jobState.TotalTestsRun < totalAllowedTests)
            {
                var pendingCandidates = jobState.Candidates
                    .Where(c => !c.IsFullyLocked && c.RetryCount < MaxRetriesPerNode)
                    .ToList();

                if (pendingCandidates.Count == 0 && jobState.PendingBatch == null)
                    break;

                List<TimingCandidate> batchToTest;

                if (jobState.PendingBatch == null)
                {
                    jobState.Iteration++;
                    context.WriteInfo($"[RefineTiming] Iteration {jobState.Iteration}: Testing {pendingCandidates.Count} candidate timings (Total tests: {jobState.TotalTestsRun}/{totalAllowedTests})...");

                    batchToTest = SelectCandidatesForBatch(pendingCandidates);
                    if (batchToTest.Count == 0)
                        break;

                    jobState.ActiveBatchStartTimes = batchToTest.Select(c => c.OriginalStartTime).ToList();

                    string fakeId = $"{transcription.Id}_refine_iter_{jobState.Iteration}";
                    jobState.PendingBatch = new Transcription(
                        id: fakeId,
                        metadataAlwaysProduced: this.InnerOptions.MetadataAlwaysProduced,
                        privateMetadataNames: null,
                        language: transcription.Language,
                        isFinished: false);
                }
                else
                {
                    context.WriteInfo($"[RefineTiming] Resuming interrupted Iteration {jobState.Iteration}...");
                    batchToTest = jobState.ActiveBatchStartTimes
                        .Select(origStart => jobState.Candidates.First(c => c.OriginalStartTime == origStart))
                        .ToList();
                }

                var itemsToBeProcessed = batchToTest
                    .Where(cand => cand.Plan != null)
                    .Select(cand => new TimedItemWithMetadata(cand.Plan.Start, cand.Plan.End, new MetadataCollection()))
                    .ToArray();

                var itemsToBeRemoved = new List<TimedItemWithMetadata>();
                foreach (var item in itemsToBeProcessed)
                {
                    if (jobState.TryGetCachedResult(item.StartTime, item.EndTime, out var result))
                    {
                        jobState.PendingBatch.AddNewItem(item.StartTime, item.EndTime, MetadataCollection.CreateSimple(this.InnerOptions.MetadataAlwaysProduced, result));
                        itemsToBeRemoved.Add(item);
                    }
                }
                itemsToBeProcessed = itemsToBeProcessed.Where(item => !itemsToBeRemoved.Contains(item)).ToArray();

                var requestGenerator = ExecuteRequests(context, jobState.PendingBatch, this.InnerOptions, itemsToBeProcessed);
                if (!requestGenerator.IsFinished())
                    return; // Interrupted

                foreach (var item in jobState.PendingBatch.Items)
                {
                    jobState.AddCachedResult(item.StartTime, item.EndTime, item.Metadata[this.InnerOptions.MetadataAlwaysProduced]);
                }

                jobState.TotalTestsRun += jobState.PendingBatch.Items.Count;
                ProcessBatchResults(jobState.Candidates, jobState.PendingBatch);
                DoExportTraceSrt(context, transcription, jobState.Candidates, "TRACE");

                transcription.Costs.AddRange(jobState.PendingBatch.Costs);
                jobState.PendingBatch = null;
                jobState.ActiveBatchStartTimes = null;
                context.WIP.Save();

                int lockedNodes = jobState.Candidates.Count(c => c.IsFullyLocked);
                context.WriteInfo($"[RefineTiming] Progress: {lockedNodes}/{jobState.Candidates.Count} fully locked.");
            }

            DoExportMetatadaSrt(context, transcription, false);
            FinalizeTranscription(transcription, jobState.Candidates);
            return; // Don't want to finalize or I can't retry easily
            transcription.MarkAsFinished();
            context.WIP.Save();
            context.WriteInfo($"[RefineTiming] Refinement completed ({jobState.TotalTestsRun} tests run).");
        }

        #region Initial bounds

        private List<TimingCandidate> InitializeCandidates(List<TimedItemWithMetadata> originalItems)
        {
            var candidates = new List<TimingCandidate>();

            foreach (var item in originalItems)
            {
                var cand = new TimingCandidate
                {
                    OriginalStartTime = item.StartTime,
                    OriginalEndTime = item.EndTime,
                    OriginalItem = item,
                    OriginalText = item.Metadata.TryGetValue(this.Options.MetadataNeeded, out var ct) ? ct : string.Empty,
                    CurrentTestStart = item.StartTime,
                    CurrentTestEnd = item.EndTime,
                    SignificantLevel = (float)(SignificantMountainRatio * r_mapping.GetPeakLevel(item.StartTime, item.EndTime))
                };
                TimeSpan start = SnapInitialBoundary(cand, BoundarySide.Start);
                TimeSpan end = SnapInitialBoundary(cand, BoundarySide.End);

                // Both boundaries ended up crossing each other: keep the original timing
                if (end <= start)
                {
                    start = item.StartTime;
                    end = item.EndTime;
                }

                EnforceMinimumDuration(cand, ref start, ref end);

                cand.CurrentTestStart = start;
                cand.CurrentTestEnd = end;
                cand.Plan = new TimeWindow(start, end, TimingMove.Init);

                cand.AddTraceRow("Orig", ' ', item.StartTime, item.EndTime, ' ', "", cand.OriginalText);
                candidates.Add(cand);
            }

            return candidates;
        }

        /// <summary>
        /// Snaps initial boundaries prioritizing grabbing whole islands:
        /// - If in silence/insignificant: jump inward to speech edge.
        /// - If inside a mountain: check outward lookahead window for energy/peaks; if present, snap
        ///   to the full outer boundary of that mountain island.
        /// </summary>
        private TimeSpan SnapInitialBoundary(TimingCandidate cand, BoundarySide side)
        {
            bool isStart = side == BoundarySide.Start;
            var t = isStart ? cand.OriginalStartTime : cand.OriginalEndTime;
            var other = isStart ? cand.OriginalEndTime : cand.OriginalStartTime;
            var maxDrift = TimeSpan.FromSeconds(MaxDriftSeconds);
            var lookahead = TimeSpan.FromSeconds(IslandPeakLookaheadSeconds);

            // 1. Silent or insignificant: snap inward towards speech edge
            if (r_mapping.IsInsignificant(t, cand.SignificantLevel))
            {
                var edge = r_mapping.FindDip(t, side, MoveDirection.Inward, new DipSearch
                {
                    MinJump = TimeSpan.MaxValue,
                    StopAtSpeechEdge = true,
                    SignificantLevel = 0,
                    Inclusive = true,
                    Limit = other
                });
                if (edge.HasValue)
                    return edge.Value;

                if (r_mapping.IsSilent(t))
                {
                    var dip = r_mapping.FindDip(t, side, MoveDirection.Inward, new DipSearch { Inclusive = true, Limit = other });
                    if (dip.HasValue && (dip.Value - t).Duration() <= maxDrift)
                        return dip.Value;
                }
                return t;
            }

            // 2. Inside a mountain: check outward island lookahead
            var lookFrom = isStart ? (t > lookahead ? t - lookahead : TimeSpan.Zero) : t;
            var lookTo = isStart ? t : t + lookahead;
            float lookaheadPeak = r_mapping.GetPeakLevel(lookFrom, lookTo);

            // If there is significant energy/peak in the outward direction, capture whole island
            if (lookaheadPeak >= cand.SignificantLevel)
            {
                var islandEdge = r_mapping.FindDip(t, side, MoveDirection.Outward, new DipSearch
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
            var snapped = r_mapping.FindDip(t, side, MoveDirection.Outward, new DipSearch { Inclusive = true });
            return snapped.HasValue && (snapped.Value - t).Duration() <= maxDrift
                ? snapped.Value
                : t;
        }

        private void EnforceMinimumDuration(TimingCandidate cand, ref TimeSpan start, ref TimeSpan end)
        {
            var minDuration = TimeSpan.FromSeconds(MinSubtitleDurationSeconds);
            var maxDrift = TimeSpan.FromSeconds(MaxDriftSeconds);

            for (int i = 0; i < 20 && end - start < minDuration; i++)
            {
                var endDip = r_mapping.FindDip(end, BoundarySide.End, MoveDirection.Outward, new DipSearch { Limit = cand.OriginalEndTime + maxDrift });
                if (endDip.HasValue)
                    end = endDip.Value;
                if (end - start >= minDuration)
                    break;

                var startDip = r_mapping.FindDip(start, BoundarySide.Start, MoveDirection.Outward, new DipSearch { Limit = cand.OriginalStartTime - maxDrift });
                if (startDip.HasValue)
                    start = startDip.Value;

                if (!endDip.HasValue && !startDip.HasValue)
                    break;
            }

            if (end - start < minDuration)
                end = start + minDuration;
        }

        #endregion

        #region Search: results -> judgement -> expansion planning

        private void ProcessBatchResults(List<TimingCandidate> batch, Transcription fakeTranscription)
        {
            var touched = new List<TimingCandidate>();

            foreach (var fakeItem in fakeTranscription.Items)
            {
                var cand = batch.FirstOrDefault(c => c.Plan != null && c.Plan.Start == fakeItem.StartTime && c.Plan.End == fakeItem.EndTime);
                if (cand == null) continue;

                string heard = fakeItem.Metadata.TryGetValue(this.InnerOptions.MetadataAlwaysProduced, out var h) ? h : string.Empty;

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
        }

        private void AdvanceCandidate(TimingCandidate cand)
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
        private void JudgeSide(TimingCandidate cand, TestedWindow test, BoundarySide side, bool degraded)
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

        private void PlanNextWindow(TimingCandidate cand, TestedWindow test)
        {
            var nextStart = PlanSide(cand, test, BoundarySide.Start);
            var nextEnd = PlanSide(cand, test, BoundarySide.End);

            if (cand.IsFullyLocked || nextEnd <= nextStart)
                return;

            if (cand.FindTest(nextStart, nextEnd) != null)
                return;

            cand.Plan = new TimeWindow(nextStart, nextEnd, TimingMove.Expand, cand.Tests.Count - 1);
        }

        private TimeSpan PlanSide(TimingCandidate cand, TestedWindow test, BoundarySide side)
        {
            var s = cand.Side(side);
            if (s.IsLocked)
                return s.Good ?? test.Boundary(side);

            var current = test.Boundary(side);
            var maxDrift = TimeSpan.FromSeconds(MaxDriftSeconds);
            var limit = side == BoundarySide.Start ? cand.OriginalStartTime - maxDrift : cand.OriginalEndTime + maxDrift;

            // Only move OUTWARD
            var next = r_mapping.FindDip(current, side, MoveDirection.Outward, new DipSearch
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

        private void UpdateCurrentBest(TimingCandidate cand)
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

        private void SetFinalNotes(TimingCandidate cand)
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

        #endregion

        private List<TimingCandidate> SelectCandidatesForBatch(List<TimingCandidate> pendingCandidates)
        {
            var batch = new List<TimingCandidate>();
            TimingCandidate lastSelected = null;

            foreach (var cand in pendingCandidates)
            {
                if (lastSelected != null && (cand.OriginalStartTime - lastSelected.OriginalEndTime).TotalMilliseconds < 250)
                {
                    if (lastSelected.IsFullyLocked)
                        continue;
                }

                batch.Add(cand);
                lastSelected = cand;
            }

            return batch;
        }

        private void FinalizeTranscription(
            Transcription transcription,
            List<TimingCandidate> candidates)
        {
            MediateOverlappingCandidates(candidates);

            var newItems = new List<TranscribedItem>();

            foreach (var cand in candidates)
            {
                TimeSpan finalStart = cand.CurrentTestStart;
                TimeSpan finalEnd = cand.CurrentTestEnd;

                var meta = new MetadataCollection();
                foreach (var kvp in cand.OriginalItem.Metadata)
                    meta[kvp.Key] = kvp.Value;

                var sb = new StringBuilder();
                if (cand.ValidationNotes.Count > 0 || !cand.IsFullyLocked)
                {
                    sb.AppendLine("Timings Warnings:");
                    if (!cand.IsFullyLocked)
                        sb.AppendLine("Unsure: Unfinished / Budget Reached");
                    foreach (var note in cand.ValidationNotes)
                        sb.AppendLine("  " + note);
                }

                if (sb.Length > 0)
                    sb.AppendLine();
                sb.AppendLine(cand.OriginalText);

                meta[this.Options.MetadataAlwaysProduced] = sb.ToString();
                meta["TimingTrace"] = string.Join("\n", cand.Trace);

                var refinedItem = new TranscribedItem(
                    finalStart,
                    finalEnd,
                    meta,
                    (cand.OriginalItem as TranscribedItem)?.NoSpeechProbability ?? 0.0,
                    (cand.OriginalItem as TranscribedItem)?.Words);

                newItems.Add(refinedItem);
            }

            transcription.Items.Clear();
            transcription.Items.AddRange(newItems);
        }

        private void MediateOverlappingCandidates(List<TimingCandidate> candidates)
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

        private void DoExportTraceSrt(
            SubtitleGeneratorContext context,
            Transcription transcription,
            IEnumerable<TimingCandidate> candidates,
            string name)
        {
            DoExportSrt(
                context,
                transcription,
                candidates.Select(cand => new Subtitle(cand.CurrentTestStart, cand.CurrentTestEnd, cand.Trace.ToArray())),
                name);
        }

        private void DoExportSrt(
            SubtitleGeneratorContext context,
            Transcription transcription,
            IEnumerable<Subtitle> subtitles,
            string name)
        {
            if (this.ExportMetadataSrt)
            {
                var extension = $".Worker.{transcription.Id}.{name}.srt";
                var virtualSubtitleFile = context.WIP.CreateVirtualSubtitleFile();
                virtualSubtitleFile.Subtitles.AddRange(subtitles);
                virtualSubtitleFile.Save(context.WIP.ParentPath, extension, context.SoftDelete);
            }
        }
    }
}