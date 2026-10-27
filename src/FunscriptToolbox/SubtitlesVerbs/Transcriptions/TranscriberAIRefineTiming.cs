using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    /// <summary>
    /// Refines the timing of an existing transcription by re-transcribing shorter / longer audio clips.
    ///
    /// Clip boundaries only ever move from one "dip" of the audio terrain to the next
    /// (see AudioTerrainMappingPhysicBased). Every window that is tested is kept, together with
    /// what the AI heard, and the final timing is picked at the end among all tests:
    /// the tightest window whose heard text is as close to the original text as the best test.
    /// </summary>
    public class TranscriberAIRefineTiming : TranscriberAI
    {
        public enum MatchResult
        {
            FullMatch,
            MissingStart,
            MissingEnd,
            MissingBoth,
            Mismatch
        }

        /// <summary>Which boundary move produced a test window.</summary>
        public enum TimingMove
        {
            Init,
            ExpandStart,
            ShrinkStart,
            ShrinkEnd,
            ExpandEnd
        }

        public enum TestWinner
        {
            First,
            Second,
            Tie
        }

        public class JobState
        {
            public int Iteration { get; set; }
            public int TotalTestsRun { get; set; }

            public List<TimingCandidate> Candidates { get; set; } = new List<TimingCandidate>();
            public List<TimeSpan> ActiveBatchStartTimes { get; set; }
            public Transcription PendingBatch { get; set; }

#if DEBUG
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
#endif
        }

        public class TimeWindow
        {
            public TimeSpan Start { get; set; }
            public TimeSpan End { get; set; }
            public TimingMove Kind { get; set; }

            [JsonIgnore] public TimeSpan Duration => End - Start;

            public TimeWindow() { }

            public TimeWindow(TimeSpan start, TimeSpan end, TimingMove kind = TimingMove.Init)
            {
                Start = start;
                End = end;
                Kind = kind;
            }
        }

        /// <summary>A window that was sent to the AI, with the answer.</summary>
        public class TestedWindow : TimeWindow
        {
            public int Round { get; set; }
            public string HeardText { get; set; }
            public MatchResult Match { get; set; }
            public double Similarity { get; set; }
        }

        public class TimingCandidate
        {
            public TimeSpan OriginalStartTime { get; set; }
            public TimeSpan OriginalEndTime { get; set; }
            public string OriginalText { get; set; }

            [JsonIgnore] public TimedItemWithMetadata OriginalItem { get; set; }

            // Best window found so far. Once the candidate is finished, this is the final timing.
            public TimeSpan CurrentTestStart { get; set; }
            public TimeSpan CurrentTestEnd { get; set; }

            // Windows waiting to be sent to the AI in the next iteration.
            public List<TimeWindow> Plan { get; set; } = new List<TimeWindow>();

            // Every window already tested, with what the AI heard. Nothing is ever discarded.
            public List<TestedWindow> Tests { get; set; } = new List<TestedWindow>();

            public bool IsStartLocked { get; set; }
            public bool IsEndLocked { get; set; }
            [JsonIgnore] public bool IsFullyLocked => IsStartLocked && IsEndLocked;

            // Number of completed rounds (a round = all windows of one plan have an answer).
            public int RetryCount { get; set; }
            public List<string> ValidationNotes { get; set; } = new List<string>();
            public List<string> Trace { get; set; } = new List<string>();

            public TestedWindow FindTest(TimeSpan start, TimeSpan end)
            {
                return Tests.FirstOrDefault(t => t.Start == start && t.End == end);
            }

            public void AddValidationNote(string note)
            {
                if (!ValidationNotes.Contains(note))
                    ValidationNotes.Add(note);
            }

            public string GetLockTag()
            {
                if (IsStartLocked && IsEndLocked) return "**";
                if (IsStartLocked) return "S?";
                if (IsEndLocked) return "?E";
                return "??";
            }

            public static char GetStartMod(TimeSpan prev, TimeSpan cur)
            {
                var diffMs = (cur - prev).TotalMilliseconds;
                if (Math.Abs(diffMs) < 1) return ' ';
                return diffMs > 0 ? '-' : '+'; // Start moving right (>0) is inward (-), moving left (<0) is outward (+)
            }

            public static char GetEndMod(TimeSpan prev, TimeSpan cur)
            {
                var diffMs = (cur - prev).TotalMilliseconds;
                if (Math.Abs(diffMs) < 1) return ' ';
                return diffMs < 0 ? '-' : '+'; // End moving left (<0) is inward (-), moving right (>0) is outward (+)
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
                    string resCol = (result ?? "").PadRight(18);
                    string textCol = text != null ? $"'{text}'" : "";
                    Trace.Add($"{lockTag} {stepCol} {boundsCol} {resCol} {textCol}".TrimEnd());
                }
            }
        }

        [JsonProperty(Order = 20)]
        public string SourceAudioIdForWaveform { get; set; }

        [JsonProperty(Order = 34, Required = Required.Always)]
        public AIOptions InnerOptions { get; set; } = new AIOptions();

        // Maximum number of rounds per candidate (a round tests 1 or 2 windows).
        [JsonProperty(Order = 43)]
        public int MaxRetriesPerNode { get; set; } = 8;

        [JsonProperty(Order = 44)]
        public double MaxBudgetRatio { get; set; } = 8.0;

        // How far (seconds) a boundary may move away from its original position.
        [JsonProperty(Order = 45)]
        public double MaxDriftSeconds { get; set; } = 0.6;

        // No longer used (boundaries now move dip to dip). Kept so existing configs still load.
        [JsonProperty(Order = 46)]
        public double ShrinkStepSeconds { get; set; } = 0.2;

        [JsonProperty(Order = 47)]
        public double MinSubtitleDurationSeconds { get; set; } = 0.5;

        // Heard text with at least this similarity (0..1) to the original text counts as a full match.
        [JsonProperty(Order = 48)]
        public double FullMatchSimilarity { get; set; } = 0.85;

        // Heard text with at least this similarity to the beginning / end of the original counts as Missing End / Start.
        [JsonProperty(Order = 49)]
        public double PartialMatchSimilarity { get; set; } = 0.80;

        // Two heard texts whose similarity to the original differ by less than this are considered equally close.
        [JsonProperty(Order = 50)]
        public double SimilarityTieTolerance { get; set; } = 0.03;

        [JsonIgnore]
        private AudioTerrainMappingPhysicBased r_mapping;

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

#if DEBUG
        private StreamWriter _writer;
#endif

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Transcription transcription)
        {
            _writer = File.CreateText(context.WIP.BaseFilePath + ".ClasssifyDebug.txt");

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
                r_mapping = new AudioTerrainMappingPhysicBased(waveformPcmAudio)
                {
                    MsPerFrame = 1,
                    BallRadiusMs = 5,
                    DropIntervalMs = 1,
                    MinDistanceTraveledMs = 5,
                    MinInterIslandSilence = TimeSpan.FromMilliseconds(300),
                    DropFloorFactor = 0.3f
                };
                r_mapping.Compute();
                context.WriteInfo($"[RefineTiming] Terrain mapping: {r_mapping.Islands.Count} islands, {r_mapping.Segments.Count} segments.");
                DoExportSrt(context, transcription, r_mapping.ToSegmentSubtitles(), "TERRAIN-MAP-PHYSIC");
                r_mapping.SaveDebugImage(context.WIP.BaseFilePath + $".terrain.jpg", TimeSpan.FromSeconds(88), TimeSpan.FromSeconds(5), 1, 700, 120);
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
                    {
                        cand.OriginalItem = item;
                    }
                }
            }

            int totalAllowedTests = Math.Max(50, (int)(originalItems.Count * MaxBudgetRatio));

            while (jobState.TotalTestsRun < totalAllowedTests)
            {
#if DEBUG
                _writer.WriteLine($"---- START ITERATION #{jobState.Iteration}");
#endif
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

#if DEBUG
                var itemToRemove = new List<TimingCandidate>();
                foreach (var cand in batchToTest)
                {
                    if (jobState.TryGetCachedResult(cand.CurrentTestStart, cand.CurrentTestEnd, out var result))
                    {
                        jobState.PendingBatch.AddNewItem(cand.CurrentTestStart, cand.CurrentTestEnd, MetadataCollection.CreateSimple(this.InnerOptions.MetadataAlwaysProduced, result));
                        itemToRemove.Add(cand);
                    }
                }
                batchToTest.RemoveAll(c => itemToRemove.Contains(c));
#endif

                if (batchToTest.Count > 0)
                {
                    // Safety net (e.g. state saved by an older version): make sure there is something to test
                    foreach (var cand in batchToTest)
                    {
                        if (cand.Plan.Count == 0)
                            cand.Plan.Add(new TimeWindow(cand.CurrentTestStart, cand.CurrentTestEnd));
                    }

                    // A candidate can have several windows to test (e.g. shrink start AND shrink end)
                    var itemsToBeProcessed = batchToTest
                        .SelectMany(cand => cand.Plan)
                        .Select(w => new TimedItemWithMetadata(w.Start, w.End, new MetadataCollection()))
                        .ToArray();

                    var requestGenerator = ExecuteRequests(context, jobState.PendingBatch, this.InnerOptions, itemsToBeProcessed);

                    if (!requestGenerator.IsFinished())
                        return; // Interrupted
                }

#if DEBUG
                foreach (var item in jobState.PendingBatch.Items)
                {
                    jobState.AddCachedResult(item.StartTime, item.EndTime, item.Metadata[this.InnerOptions.MetadataAlwaysProduced]);
                }
                context.WIP.Save();
#endif

                jobState.TotalTestsRun += jobState.PendingBatch.Items.Count;
                ProcessBatchResults(jobState.Candidates, jobState.PendingBatch);
                DoExportTraceSrt(context, transcription, jobState.Candidates, "TRACE");

                transcription.Costs.AddRange(jobState.PendingBatch.Costs);
                jobState.PendingBatch = null;
                jobState.ActiveBatchStartTimes = null;
                context.WIP.Save();

#if DEBUG
                var dict = new Dictionary<MatchResult, int>();
                var nomoves = 0;
                foreach (var cand in jobState.Candidates)
                {
                    var match = cand.Tests.LastOrDefault()?.Match;
                    if (match != null)
                    {
                        if (!dict.TryGetValue(match.Value, out var kindCount))
                        {
                            dict[match.Value] = 1;
                        }
                        else
                        {
                            dict[match.Value] = dict[match.Value] + 1;
                        }
                    }
                    else
                    {
                        nomoves++;
                    }
                }
                _writer.WriteLine($"--------------- END ITERATION #{jobState.Iteration}");
                foreach (var kvp in dict)
                {
                    _writer.WriteLine($"{kvp.Key} = {kvp.Value}");
                }
                _writer.WriteLine($"No moves = {nomoves}");
                _writer.Flush();
#endif
                int lockedNodes = jobState.Candidates.Count(c => c.IsFullyLocked);
                context.WriteInfo($"[RefineTiming] Progress: {lockedNodes}/{jobState.Candidates.Count} fully locked.");
            }

#if DEBUG
            _writer.Close();
#endif
            return; // Don't want to finalize or I can't retry easily
            FinalizeTranscription(transcription, jobState.Candidates);
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
                    CurrentTestEnd = item.EndTime
                };

                TimeSpan start = SnapStart(cand);
                TimeSpan end = SnapEnd(cand);

                // Both boundaries ended up in the same stretch of silence: keep the original timing
                if (end <= start)
                {
                    start = item.StartTime;
                    end = item.EndTime;
                }

                EnforceMinimumDuration(cand, ref start, ref end);

                cand.CurrentTestStart = start;
                cand.CurrentTestEnd = end;
                cand.Plan.Add(new TimeWindow(start, end, TimingMove.Init));

                cand.AddTraceRow("Orig", ' ', item.StartTime, item.EndTime, ' ', "", cand.OriginalText);

                candidates.Add(cand);
            }

            return candidates;
        }

        /// <summary>
        /// In silence: move inward to the first mountain.
        /// Inside a mountain: slide outward to the dip that starts it.
        /// </summary>
        private TimeSpan SnapStart(TimingCandidate cand)
        {
            var t = cand.OriginalStartTime;
            TimeSpan? snapped = r_mapping.IsSilent(t)
                ? r_mapping.SnapStartInward(t)
                : r_mapping.SnapStartOutward(t);

            return snapped.HasValue && (snapped.Value - t).Duration() <= TimeSpan.FromSeconds(MaxDriftSeconds)
                ? snapped.Value
                : t;
        }

        /// <summary>
        /// In silence: move inward to the end of the last mountain.
        /// Inside a mountain: slide outward to the dip that ends it.
        /// </summary>
        private TimeSpan SnapEnd(TimingCandidate cand)
        {
            var t = cand.OriginalEndTime;
            TimeSpan? snapped = r_mapping.IsSilent(t)
                ? r_mapping.SnapEndInward(t)
                : r_mapping.SnapEndOutward(t);

            return snapped.HasValue && (snapped.Value - t).Duration() <= TimeSpan.FromSeconds(MaxDriftSeconds)
                ? snapped.Value
                : t;
        }

        private void EnforceMinimumDuration(TimingCandidate cand, ref TimeSpan start, ref TimeSpan end)
        {
            var minDuration = TimeSpan.FromSeconds(MinSubtitleDurationSeconds);

            for (int i = 0; i < 20 && end - start < minDuration; i++)
            {
                var endDip = NextDip(cand, new TimeWindow(start, end), isStart: false, outward: true);
                if (endDip.HasValue)
                    end = endDip.Value;
                if (end - start >= minDuration)
                    break;

                var startDip = NextDip(cand, new TimeWindow(start, end), isStart: true, outward: true);
                if (startDip.HasValue)
                    start = startDip.Value;

                if (!endDip.HasValue && !startDip.HasValue)
                    break;
            }

            // Still too short (no more dips within MaxDrift): pad the end
            if (end - start < minDuration)
                end = start + minDuration;
        }

        #endregion

        #region Search: results -> decision -> next windows

        private void ProcessBatchResults(List<TimingCandidate> batch, Transcription fakeTranscription)
        {
            var touched = new List<TimingCandidate>();

            foreach (var fakeItem in fakeTranscription.Items)
            {
                var cand = batch.FirstOrDefault(c => c.Plan.Any(w => w.Start == fakeItem.StartTime && w.End == fakeItem.EndTime));
                if (cand == null) continue;

                var window = cand.Plan.First(w => w.Start == fakeItem.StartTime && w.End == fakeItem.EndTime);
                string heard = fakeItem.Metadata.TryGetValue(this.InnerOptions.MetadataAlwaysProduced, out var h) ? h : string.Empty;

                var test = new TestedWindow
                {
                    Start = window.Start,
                    End = window.End,
                    Kind = window.Kind,
                    Round = cand.RetryCount,
                    HeardText = heard,
                    Match = Classify(cand.OriginalText, heard),
                    Similarity = TextSimilarity(cand.OriginalText, heard)
                };
#if DEBUG
                _writer.WriteLine($"Original: {cand.OriginalText}");
                _writer.WriteLine($"Heard:    {heard}");
                _writer.WriteLine($"{cand.OriginalStartTime} => {test.Match}");
#endif

                // Trace: +/- shown relative to the window this one was derived from
                bool isInit = test.Kind == TimingMove.Init;
                char startMod = isInit
                    ? TimingCandidate.GetStartMod(cand.OriginalStartTime, test.Start)
                    : TimingCandidate.GetStartMod(cand.CurrentTestStart, test.Start);
                char endMod = isInit
                    ? TimingCandidate.GetEndMod(cand.OriginalEndTime, test.End)
                    : TimingCandidate.GetEndMod(cand.CurrentTestEnd, test.End);

                cand.Tests.Add(test);
                cand.Plan.Remove(window);

                string stepName = cand.Tests.Count == 1 ? "Init" : $"#{cand.Tests.Count - 1}";
                cand.AddTraceRow(stepName, startMod, test.Start, test.End, endMod, $"{test.Match} {test.Similarity:0.00}", heard);

                if (!touched.Contains(cand))
                    touched.Add(cand);
            }

            foreach (var cand in touched)
            {
                // Some windows of this round are still waiting for their answer: decide next time
                if (cand.Plan.Count > 0)
                    continue;

                AdvanceCandidate(cand);
            }
        }

        /// <summary>
        /// Called when every window of the current round has an answer.
        /// Locks the sides that cannot improve, picks the best window so far and plans the next round.
        /// </summary>
        private void AdvanceCandidate(TimingCandidate cand)
        {
            var previousBest = cand.FindTest(cand.CurrentTestStart, cand.CurrentTestEnd);
            var roundTests = cand.Tests.Where(t => t.Round == cand.RetryCount).ToList();
            cand.RetryCount++;

            // 1. Judge each move tried in this round
            var eligible = GetEligibleTests(cand);
            foreach (var test in roundTests)
            {
                switch (test.Kind)
                {
                    case TimingMove.ShrinkStart:
                        if (!eligible.Contains(test)) cand.IsStartLocked = true;
                        break;
                    case TimingMove.ShrinkEnd:
                        if (!eligible.Contains(test)) cand.IsEndLocked = true;
                        break;
                    case TimingMove.ExpandStart:
                        if (!IsGain(cand, previousBest, test)) cand.IsStartLocked = true;
                        break;
                    case TimingMove.ExpandEnd:
                        if (!IsGain(cand, previousBest, test)) cand.IsEndLocked = true;
                        break;
                }
            }

            // 2. Best window among ALL tests so far
            var best = PickBest(cand);

            if (best.Start != cand.CurrentTestStart || best.End != cand.CurrentTestEnd)
            {
                cand.AddTraceRow(
                    "Best",
                    TimingCandidate.GetStartMod(cand.CurrentTestStart, best.Start),
                    best.Start,
                    best.End,
                    TimingCandidate.GetEndMod(cand.CurrentTestEnd, best.End));
            }

            cand.CurrentTestStart = best.Start;
            cand.CurrentTestEnd = best.End;

            // 3. Plan the next round (or stop)
            cand.Plan.Clear();
            if (cand.RetryCount < MaxRetriesPerNode)
                PlanNextMoves(cand, best);

            if (cand.Plan.Count == 0)
            {
                cand.IsStartLocked = true;
                cand.IsEndLocked = true;
                SetFinalNotes(cand, best);
            }
        }

        /// <summary>
        /// Tests whose heard text is as close to the original text as the closest one,
        /// restricted to tests that cover the whole text (FullMatch / FullExtra).
        /// </summary>
        private List<TestedWindow> GetEligibleTests(TimingCandidate cand)
        {
            var pool = cand.Tests.Where(t => t.Match == MatchResult.FullMatch).ToList();
            if (pool.Count == 0)
                return new List<TestedWindow>();

            var top = pool.OrderByDescending(t => t.Similarity).First();
            return pool
                .Where(t => EvaluateMatch(cand.OriginalText, top.HeardText, t.HeardText) != TestWinner.First)
                .ToList();
        }

        /// <summary>
        /// Final choice: the tightest window among the eligible tests.
        /// While no test covers the whole text yet, the test closest to the original text.
        /// </summary>
        private TestedWindow PickBest(TimingCandidate cand)
        {
            var eligible = GetEligibleTests(cand);
            if (eligible.Count > 0)
                return eligible.OrderBy(t => t.Duration).First();

            return cand.Tests
                .OrderByDescending(t => t.Similarity)
                .ThenByDescending(t => t.Duration)
                .First();
        }

        /// <summary>An expansion is worth continuing if it brought us closer to the original text.</summary>
        private bool IsGain(TimingCandidate cand, TestedWindow previousBest, TestedWindow test)
        {
            if (previousBest == null)
                return true;
            if (test.Match == MatchResult.FullMatch && previousBest.Match != MatchResult.FullMatch)
                return true;
            return EvaluateMatch(cand.OriginalText, previousBest.HeardText, test.HeardText) == TestWinner.Second;
        }

        private void PlanNextMoves(TimingCandidate cand, TestedWindow best)
        {
            switch (best.Match)
            {
                case MatchResult.FullMatch:
                    // Everything was heard: try to tighten both sides (kept as separate tests)
                    PlanMove(cand, best, TimingMove.ShrinkStart);
                    PlanMove(cand, best, TimingMove.ShrinkEnd);
                    break;

                case MatchResult.MissingStart:
                    PlanMove(cand, best, TimingMove.ExpandStart);
                    break;

                case MatchResult.MissingEnd:
                    PlanMove(cand, best, TimingMove.ExpandEnd);
                    break;

                case MatchResult.MissingBoth:
                    PlanMove(cand, best, TimingMove.ExpandStart);
                    PlanMove(cand, best, TimingMove.ExpandEnd);
                    break;

                case MatchResult.Mismatch:
                    if (best.Kind == TimingMove.Init)
                    {
                        PlanMove(cand, best, TimingMove.ExpandStart);
                        PlanMove(cand, best, TimingMove.ExpandEnd);
                    }
                    else
                    {
                        var m = 0;
                    }
                    break; // nothing sensible to try
            }
        }

        private void PlanMove(TimingCandidate cand, TestedWindow from, TimingMove kind)
        {
            bool isStart = kind == TimingMove.ExpandStart || kind == TimingMove.ShrinkStart;
            bool outward = kind == TimingMove.ExpandStart ||  kind == TimingMove.ExpandEnd;

            if (isStart ? cand.IsStartLocked : cand.IsEndLocked)
                return;

            TimeSpan? dip = NextDip(cand, from, isStart, outward);

            TimeWindow next = null;
            if (dip.HasValue)
            {
                next = new TimeWindow(
                    isStart ? dip.Value : from.Start,
                    isStart ? from.End : dip.Value,
                    kind);
            }

            // No dip left, or this exact window was already tested: nothing new to learn on this side
            if (next == null || cand.FindTest(next.Start, next.End) != null)
            {
                if (isStart) cand.IsStartLocked = true; else cand.IsEndLocked = true;
                return;
            }

            cand.Plan.Add(next);
        }

        private void SetFinalNotes(TimingCandidate cand, TestedWindow best)
        {
            cand.ValidationNotes.Clear();

            switch (best.Match)
            {
                case MatchResult.MissingStart:
                    cand.AddValidationNote("Unsure: Missing Start");
                    break;
                case MatchResult.MissingEnd:
                    cand.AddValidationNote("Unsure: Missing End");
                    break;
                case MatchResult.MissingBoth:
                    cand.AddValidationNote("Unsure: Clipped Both Sides");
                    break;
                case MatchResult.Mismatch:
                    cand.AddValidationNote(string.IsNullOrWhiteSpace(best.HeardText)
                        ? "Unsure: Audio Inaudible"
                        : $"Unsure: Text Mismatch: {best.HeardText}");
                    break;
            }
        }

        #endregion

        #region Dips

        /// <summary>
        /// Next boundary position for one side of a window: the first dip inward or outward.
        /// Dips that are out of range, or that IsDipUseful rejects, are skipped.
        /// Returns null when there is none.
        /// </summary>
        private TimeSpan? NextDip(TimingCandidate cand, TimeWindow window, bool isStart, bool outward)
        {
            var maxDrift = TimeSpan.FromSeconds(MaxDriftSeconds);
            var minDuration = TimeSpan.FromSeconds(MinSubtitleDurationSeconds);

            TimeSpan position = isStart ? window.Start : window.End;

            for (int guard = 0; guard < 50; guard++)
            {
                TimeSpan? next;
                if (isStart)
                    next = outward ? r_mapping.ExpandStart(position) : r_mapping.ShrinkStart(position);
                else
                    next = outward ? r_mapping.ExpandEnd(position) : r_mapping.ShrinkEnd(position);

                if (next == null)
                    return null;

                // Limits: stay within MaxDrift of the original timing, never make the clip too short
                if (isStart && outward && next.Value < cand.OriginalStartTime - maxDrift) return null;
                if (!isStart && outward && next.Value > cand.OriginalEndTime + maxDrift) return null;
                if (isStart && !outward && window.End - next.Value < minDuration) return null;
                if (!isStart && !outward && next.Value - window.Start < minDuration) return null;

                if (IsDipUseful(cand, window, isStart, outward, next.Value))
                    return next;

                position = next.Value; // skip this dip, look at the next one
            }

            return null;
        }

        /// <summary>
        /// THE place to decide that a dip "doesn't make sense" for a specific case and must be skipped.
        /// Called for every dip NextDip finds; return false to jump over it.
        /// Available context: the candidate (original text/timing, tests so far), the window being
        /// moved, which side moves, the direction, and the dip position.
        /// </summary>
        private bool IsDipUseful(TimingCandidate cand, TimeWindow window, bool isStart, bool outward, TimeSpan dip)
        {
            return true;
        }

        #endregion

        #region Matching

        private static readonly Regex PunctuationNormalizeRegex = new Regex(@"[.!…]+", RegexOptions.Compiled);
        private static readonly Regex WhitespaceNormalizeRegex = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex HyphenStripRegex = new Regex(@"(?<=\p{L})-(?=\p{L})", RegexOptions.Compiled);

        /// <summary>
        /// Which of two tests is closest to the original text?
        /// First  = heardFirst is clearly closer,
        /// Second = heardSecond is clearly closer,
        /// Tie    = equally close (difference within SimilarityTieTolerance).
        /// Fuzzy, because the second AI pass rarely returns exactly what the first one heard.
        /// </summary>
        private TestWinner EvaluateMatch(string candidateText, string heardFirst, string heardSecond)
        {
            double a = TextSimilarity(candidateText, heardFirst);
            double b = TextSimilarity(candidateText, heardSecond);

            if (Math.Abs(a - b) <= SimilarityTieTolerance)
                return TestWinner.Tie;

            return a > b ? TestWinner.First : TestWinner.Second;
        }

        private struct SyllableAlignItem
        {
            public int CandIndex;
            public int HeardIndex;
            public double Similarity;
        }

        /// <summary>
        /// What does the heard text say about the clip?
        /// Uses sequence diff/alignment on syllables to classify whether the clip contains
        /// the full candidate, extra speech, clipped start, clipped end, or is a mismatch.
        /// </summary>
        private MatchResult Classify(string candidateText, string heardText)
        {
            if (string.IsNullOrWhiteSpace(candidateText) || string.IsNullOrWhiteSpace(heardText))
                return MatchResult.Mismatch;

            var cList = ExtractSyllables(candidateText);
            var hList = ExtractSyllables(heardText);

            if (cList.Count == 0 || hList.Count == 0)
                return MatchResult.Mismatch;

            // Fast path: exact syllable sequence match
            if (cList.Count == hList.Count)
            {
                bool allSame = true;
                for (int i = 0; i < cList.Count; i++)
                {
                    if (cList[i] != hList[i])
                    {
                        allSame = false;
                        break;
                    }
                }
                if (allSame)
                    return MatchResult.FullMatch;
            }

            int n = cList.Count;
            int m = hList.Count;

            // DP diff alignment matrix: d[i, j] = minimum edit cost to align cList[0..i-1] with hList[0..j-1]
            var d = new double[n + 1, m + 1];
            var dir = new byte[n + 1, m + 1]; // 1 = diag (aligned), 2 = up (cand deletion), 3 = left (heard insertion)

            for (int i = 0; i <= n; i++)
            {
                d[i, 0] = i * 1.0;
                dir[i, 0] = 2;
            }
            for (int j = 0; j <= m; j++)
            {
                d[0, j] = j * 1.0;
                dir[0, j] = 3;
            }
            dir[0, 0] = 0;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    double sim = SingleSyllableSimilarity(cList[i - 1], hList[j - 1]);
                    double subCost = (sim >= 0.25) ? 2.0 * (1.0 - sim) : 2.5;

                    double costDiag = d[i - 1, j - 1] + subCost;
                    double costUp = d[i - 1, j] + 1.0;   // cand deleted
                    double costLeft = d[i, j - 1] + 1.0; // heard inserted

                    if (costDiag <= costUp && costDiag <= costLeft)
                    {
                        d[i, j] = costDiag;
                        dir[i, j] = 1;
                    }
                    else if (costUp <= costLeft)
                    {
                        d[i, j] = costUp;
                        dir[i, j] = 2;
                    }
                    else
                    {
                        d[i, j] = costLeft;
                        dir[i, j] = 3;
                    }
                }
            }

            // Traceback from (n, m) to (0, 0)
            var align = new List<SyllableAlignItem>(n + m);
            int curI = n, curJ = m;
            while (curI > 0 || curJ > 0)
            {
                byte step = dir[curI, curJ];
                if (step == 1)
                {
                    double sim = SingleSyllableSimilarity(cList[curI - 1], hList[curJ - 1]);
                    align.Add(new SyllableAlignItem { CandIndex = curI - 1, HeardIndex = curJ - 1, Similarity = sim });
                    curI--;
                    curJ--;
                }
                else if (step == 2)
                {
                    align.Add(new SyllableAlignItem { CandIndex = curI - 1, HeardIndex = -1, Similarity = 0 });
                    curI--;
                }
                else
                {
                    align.Add(new SyllableAlignItem { CandIndex = -1, HeardIndex = curJ - 1, Similarity = 0 });
                    curJ--;
                }
            }
            align.Reverse();

            // Find matching syllables
            double matchScore = 0;
            int firstMatchIdx = -1;
            int lastMatchIdx = -1;

            for (int k = 0; k < align.Count; k++)
            {
                var item = align[k];
                if (item.CandIndex >= 0 && item.HeardIndex >= 0 && item.Similarity >= 0.35)
                {
                    matchScore += item.Similarity;
                    if (firstMatchIdx < 0)
                        firstMatchIdx = k;
                    lastMatchIdx = k;
                }
            }

            if (firstMatchIdx < 0)
                return MatchResult.Mismatch;

            double candCoverage = matchScore / n;
            double heardCoverage = matchScore / m;

            // If neither sequence has sufficient correspondence, it's a mismatch
            if (candCoverage < 0.35 && heardCoverage < 0.35)
                return MatchResult.Mismatch;

            int firstCand = align[firstMatchIdx].CandIndex;
            int firstHeard = align[firstMatchIdx].HeardIndex;
            int lastCand = align[lastMatchIdx].CandIndex;
            int lastHeard = align[lastMatchIdx].HeardIndex;

            int missingStart = firstCand;
            int extraStart = firstHeard;
            int missingEnd = (n - 1) - lastCand;
            int extraEnd = (m - 1) - lastHeard;

            bool hasMissingStart = missingStart > 0;
            bool hasMissingEnd = missingEnd > 0;
            bool hasExtraStart = extraStart > 0;
            bool hasExtraEnd = extraEnd > 0;

            if (hasMissingStart && hasMissingEnd)
                return MatchResult.MissingBoth;
            if (hasMissingStart)
                return MatchResult.MissingStart;
            if (hasMissingEnd)
                return MatchResult.MissingEnd;
            if (hasExtraStart || hasExtraEnd)
                return MatchResult.FullMatch;

            return MatchResult.FullMatch;
        }

        private string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string s = text.Trim().ToLowerInvariant();
            s = HyphenStripRegex.Replace(s, "");
            s = PunctuationNormalizeRegex.Replace(s, ".");
            s = WhitespaceNormalizeRegex.Replace(s, " ");
            return s;
        }

        /// <summary>0 (nothing in common) .. 1 (identical), ignoring punctuation, spacing and case.</summary>
        private static double TextSimilarity(string a, string b)
        {
            return SyllableSequenceSimilarity(ExtractSyllables(a), ExtractSyllables(b));
        }

        private static List<string> Slice(List<string> list, int start, int count)
        {
            var res = new List<string>(count);
            for (int i = 0; i < count; i++)
                res.Add(list[start + i]);
            return res;
        }

        /// <summary>
        /// Splits Romaji (hyphenated or unhyphenated) into normalized canonical morae/syllables.
        /// Examples: "Se-n-pa-i" / "Sen-pai" / "Senpai" -> ["se", "n", "pa", "i"]
        ///           "Ki-mo-chi-i" / "Ki-mo-chii"       -> ["ki", "mo", "chi", "i"]
        ///           "I-ppa-i" / "Ip-pa-i"              -> ["i", "pa", "i"]
        /// </summary>
        private static List<string> ExtractSyllables(string text)
        {
            var syllables = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return syllables;

            var tokenBuilder = new StringBuilder();
            foreach (char ch in text)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    tokenBuilder.Append(char.ToLowerInvariant(ch));
                }
                else if (tokenBuilder.Length > 0)
                {
                    AppendTokenMorae(tokenBuilder.ToString(), syllables);
                    tokenBuilder.Clear();
                }
            }
            if (tokenBuilder.Length > 0)
                AppendTokenMorae(tokenBuilder.ToString(), syllables);

            return syllables;
        }

        private static void AppendTokenMorae(string token, List<string> syllables)
        {
            // Normalize trailing breath 'h' after a vowel (e.g. "ah" -> "a", "oh" -> "o")
            if (token.Length == 2 && IsRomajiVowel(token[0]) && token[1] == 'h')
            {
                syllables.Add(token[0].ToString());
                return;
            }

            int i = 0;
            while (i < token.Length)
            {
                char c = token[i];

                if (char.IsDigit(c))
                {
                    int start = i;
                    while (i < token.Length && char.IsDigit(token[i])) i++;
                    syllables.Add(token.Substring(start, i - start));
                    continue;
                }

                // 1. Standalone vowel mora (a, i, u, e, o)
                if (IsRomajiVowel(c))
                {
                    syllables.Add(c.ToString());
                    i++;
                    continue;
                }

                // 2. Moraic 'n' (at end of token, or before a non-vowel and non-'y')
                if (c == 'n' && (i + 1 == token.Length || (!IsRomajiVowel(token[i + 1]) && token[i + 1] != 'y')))
                {
                    syllables.Add("n");
                    i++;
                    // Skip doubled 'nn' inside the same dash-token if followed by a consonant or end
                    if (i < token.Length && token[i] == 'n' && (i + 1 == token.Length || (!IsRomajiVowel(token[i + 1]) && token[i + 1] != 'y')))
                        i++;
                    continue;
                }

                // 3. Geminate double consonant (e.g. "ppa" -> "pa", "tte" -> "te", "tch" -> "ch")
                if (i + 1 < token.Length && c == token[i + 1] && c != 'n')
                {
                    i++;
                    continue;
                }
                if (c == 't' && i + 2 < token.Length && token[i + 1] == 'c' && token[i + 2] == 'h')
                {
                    i++;
                    continue;
                }

                // 4. Consonant cluster + vowel
                int onsetStart = i;
                while (i < token.Length && !IsRomajiVowel(token[i]) && !char.IsDigit(token[i]))
                {
                    i++;
                }

                if (i < token.Length && IsRomajiVowel(token[i]))
                {
                    string rawMora = token.Substring(onsetStart, i - onsetStart + 1);
                    i++;
                    // Skip trailing silent 'h' in interjections like "haa" / "ah" if written "hah"
                    if (i < token.Length && token[i] == 'h' && (i + 1 == token.Length || !IsRomajiVowel(token[i + 1])))
                        i++;
                    syllables.Add(NormalizeMora(rawMora));
                }
                else
                {
                    // Trailing consonant with no vowel (e.g. "ip" from "ip-pa-i"): ignore geminate tail
                    break;
                }
            }
        }

        private static bool IsRomajiVowel(char c)
        {
            return c == 'a' || c == 'i' || c == 'u' || c == 'e' || c == 'o';
        }

        private static string NormalizeMora(string mora)
        {
            switch (mora)
            {
                case "si": return "shi";
                case "ti": return "chi";
                case "tu": return "tsu";
                case "hu": return "fu";
                case "zi":
                case "di": return "ji";
                case "du": return "zu";
                case "sya": return "sha";
                case "syu": return "shu";
                case "syo": return "sho";
                case "tya": return "cha";
                case "tyu": return "chu";
                case "tyo": return "cho";
                case "zya": return "ja";
                case "zyu": return "ju";
                case "zyo": return "jo";
                default: return mora;
            }
        }

        private static double SyllableSequenceSimilarity(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count == 0 && b.Count == 0) return 1.0;
            if (a.Count == 0 || b.Count == 0) return 0.0;
            return 1.0 - SyllableLevenshtein(a, b) / Math.Max(a.Count, b.Count);
        }

        private static double SyllableLevenshtein(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            var prev = new double[b.Count + 1];
            var cur = new double[b.Count + 1];

            for (int j = 0; j <= b.Count; j++)
                prev[j] = j;

            for (int i = 1; i <= a.Count; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Count; j++)
                {
                    double subCost = 1.0 - SingleSyllableSimilarity(a[i - 1], b[j - 1]);
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1.0, prev[j] + 1.0), prev[j - 1] + subCost);
                }
                var tmp = prev; prev = cur; cur = tmp;
            }

            return prev[b.Count];
        }

        /// <summary>
        /// Graduated similarity between two individual syllables/morae (0.0 .. 1.0).
        /// Rewards identical vowels and phonetically close consonants when audio is slurred/breathless.
        /// </summary>
        private static double SingleSyllableSimilarity(string a, string b)
        {
            if (a == b) return 1.0;
            if (a.Length == 0 || b.Length == 0) return 0.0;

            char vowelA = a[a.Length - 1];
            char vowelB = b[b.Length - 1];
            bool hasVowelA = IsRomajiVowel(vowelA);
            bool hasVowelB = IsRomajiVowel(vowelB);

            if (!hasVowelA || !hasVowelB)
            {
                // Moraic 'n' vs nasal syllable ('na', 'ni', 'nu', 'ne', 'no', 'mu')
                if ((a == "n" && b[0] == 'n') || (b == "n" && a[0] == 'n') || (a == "n" && b == "mu") || (b == "n" && a == "mu"))
                    return 0.4;
                return 0.0;
            }

            string onsetA = a.Substring(0, a.Length - 1);
            string onsetB = b.Substring(0, b.Length - 1);

            if (vowelA == vowelB)
            {
                if (ArePhoneticallyCloseOnsets(onsetA, onsetB))
                    return 0.75;
                return 0.50;
            }

            if (onsetA.Length > 0 && onsetA == onsetB)
                return 0.40;

            return 0.0;
        }

        private static bool ArePhoneticallyCloseOnsets(string onsetA, string onsetB)
        {
            if (onsetA == onsetB) return true;

            // Breath/aspirate vs pure vowel ("ha" vs "a", "fu" vs "u")
            if ((onsetA.Length == 0 && (onsetB == "h" || onsetB == "f")) ||
                (onsetB.Length == 0 && (onsetA == "h" || onsetA == "f")))
                return true;

            return GetOnsetGroup(onsetA) == GetOnsetGroup(onsetB) && GetOnsetGroup(onsetA) != 0;
        }

        private static int GetOnsetGroup(string onset)
        {
            switch (onset)
            {
                case "k":
                case "g":
                case "ky":
                case "gy":
                    return 1; // Velars
                case "s":
                case "sh":
                case "z":
                case "j":
                case "ts":
                case "ch":
                    return 2; // Sibilants & affricates
                case "t":
                case "d":
                case "n":
                case "ny":
                case "r":
                case "ry":
                case "m":
                case "my":
                case "b":
                case "by":
                case "p":
                case "py":
                case "h":
                case "hy":
                case "f":
                case "w":
                    return 3; // Labials / coronals / liquids (commonly slurred in muffled speech, e.g. bi/ri)
                default:
                    return 0;
            }
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
                {
                    meta[kvp.Key] = kvp.Value;
                }

                var sb = new StringBuilder();

                // Clean nodes have no TimingValidation key, keeping the subtitle clean.
                if (cand.ValidationNotes.Count > 0 || !cand.IsFullyLocked)
                {
                    sb.AppendLine("Timings Warnings:");
                    // If global test budget ran out before this candidate finished testing
                    if (!cand.IsFullyLocked)
                    {
                        sb.AppendLine("Unsure: Unfinished / Budget Reached");
                    }
                    foreach (var note in cand.ValidationNotes)
                    {
                        sb.AppendLine("  " + note);
                    }
                }

                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }
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
                    {
                        midpoint = curStart + minDuration;
                    }
                    else if (nextEnd - midpoint < minDuration)
                    {
                        midpoint = nextEnd - minDuration;
                    }

                    if (midpoint <= curStart || midpoint >= nextEnd)
                    {
                        midpoint = TimeSpan.FromTicks(midTicks);
                    }

                    char curEndMod = TimingCandidate.GetEndMod(curEnd, midpoint);
                    char nextStartMod = TimingCandidate.GetStartMod(nextStart, midpoint);

                    cur.CurrentTestEnd = midpoint;
                    next.CurrentTestStart = midpoint;

                    // Only flag for manual review if the overlap was meaningful (>= 50ms)
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

                var fullfilePath = context.WIP.BaseFilePath + extension;
                //if (context.WIP.TimelineMap.IsMultipart)
                //{
                //    var newFullFile = new SubtitleFile();
                //    newFullFile.Subtitles.AddRange(
                //        virtualSubtitleFile.Subtitles.Select(item =>
                //            new Subtitle(item.StartTime, item.EndTime, item.Text)));
                //    newFullFile.SaveSrt(fullfilePath);
                //}
            }
        }
    }
}
