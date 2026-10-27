using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
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
        /// <summary>Which boundary move produced a test window.</summary>
        public enum TimingMove
        {
            Init,
            Refine,
            ExpandStart,
            ShrinkStart,
            ShrinkEnd,
            ExpandEnd
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

            // Index (in TimingCandidate.Tests) of the test this window was derived from (-1 = none / Init).
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

            // The AI claimed syllables were lost on that side, but it is not physically plausible.
            public bool SuspiciousStart { get; set; }
            public bool SuspiciousEnd { get; set; }

            public bool IsSuspicious(BoundarySide side) => side == BoundarySide.Start ? SuspiciousStart : SuspiciousEnd;
            [JsonIgnore] public bool IsAnySuspicious => SuspiciousStart || SuspiciousEnd;
        }

        /// <summary>Search state of one boundary (start or end) of a candidate.</summary>
        public class SideSearch
        {
            // Tightest boundary known to cover the speech on that side.
            public TimeSpan? Good { get; set; }

            // Boundary closest to Good known to cut the speech on that side.
            public TimeSpan? Bad { get; set; }

            public bool IsLocked { get; set; }
            public bool LockedBySuspicion { get; set; }
            public int SuspiciousStreak { get; set; }
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

            // Number of completed rounds (a round = the planned window has an answer).
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

        // No longer used (boundaries now move dip to dip). Kept so existing configs still load.
        [JsonProperty(Order = 46)]
        public double ShrinkStepSeconds { get; set; } = 0.2;

        [JsonProperty(Order = 47)]
        public double MinSubtitleDurationSeconds { get; set; } = 0.5;

        // No longer used (per-side states replaced it). Kept so existing configs still load.
        [JsonProperty(Order = 48)]
        public double FullMatchSimilarity { get; set; } = 0.85;

        // No longer used (per-side states replaced it). Kept so existing configs still load.
        [JsonProperty(Order = 49)]
        public double PartialMatchSimilarity { get; set; } = 0.80;

        // Two heard texts whose similarity to the original differ by less than this are considered equally close.
        [JsonProperty(Order = 50)]
        public double SimilarityTieTolerance { get; set; } = 0.03;

        // A side with N missing syllables and at least N extra ones is read as a substitution (side covered)
        // only when N is at most this value.
        [JsonProperty(Order = 51)]
        public int MaxSubstitutedSyllables { get; set; } = 2;

        // A boundary move must be at least this long, unless it stops exactly on a speech edge.
        [JsonProperty(Order = 52)]
        public double MinJumpSeconds { get; set; } = 0.07;

        // A mountain whose peak is below this ratio of the loudest level inside the original subtitle is noise.
        [JsonProperty(Order = 53)]
        public double SignificantMountainRatio { get; set; } = 0.15;

        // Shortest plausible syllable: detects AI answers that lost more syllables than the removed audio can hold.
        [JsonProperty(Order = 54)]
        public double MinSyllableDurationSeconds { get; set; } = 0.06;

        // Consecutive suspicious answers on one side before that side is locked.
        [JsonProperty(Order = 55)]
        public int MaxSuspiciousPerSide { get; set; } = 2;

        // A bisect probe must be at least this far from both the good and the bad boundary.
        [JsonProperty(Order = 56)]
        public double BisectMinGapSeconds { get; set; } = 0.02;

        // A test whose similarity is this much lower than the best similarity so far does not count as covering.
        [JsonProperty(Order = 57)]
        public double MaxSimilarityDrop { get; set; } = 0.3;

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
#if DEBUG
            _writer = File.CreateText(context.WIP.BaseFilePath + ".ClasssifyDebug.txt");
#endif

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


                // Windows that still need to be processed by the AI (not satisfied by cache)
                var itemsToBeProcessed = batchToTest
                    .Where(cand => cand.Plan != null)
                    .Select(cand => new TimedItemWithMetadata(cand.Plan.Start, cand.Plan.End, new MetadataCollection()))
                    .ToArray();

#if DEBUG
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
#endif

                var requestGenerator = ExecuteRequests(context, jobState.PendingBatch, this.InnerOptions, itemsToBeProcessed);

                if (!requestGenerator.IsFinished())
                    return; // Interrupted

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
                var dict = new Dictionary<string, int>();
                var nomoves = 0;
                foreach (var cand in jobState.Candidates)
                {
                    var match = cand.Tests.LastOrDefault()?.Match;
                    if (match != null)
                    {
                        var key = match.IsMismatch ? "Mismatch" : $"S={match.StartState} E={match.EndState}";
                        dict.TryGetValue(key, out var kindCount);
                        dict[key] = kindCount + 1;
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
        /// In silence or insignificant terrain (noise): move INWARD to the speech edge, i.e. the dip where
        /// the first (start) / last (end) significant mountain begins / ends. MaxDrift is ignored, the
        /// move is only bounded by the other original boundary.
        /// Inside a significant mountain: slide OUTWARD to the dip that starts / ends it (within MaxDrift).
        /// </summary>
        private TimeSpan SnapInitialBoundary(TimingCandidate cand, BoundarySide side)
        {
            bool isStart = side == BoundarySide.Start;
            var t = isStart ? cand.OriginalStartTime : cand.OriginalEndTime;
            var other = isStart ? cand.OriginalEndTime : cand.OriginalStartTime;
            var maxDrift = TimeSpan.FromSeconds(MaxDriftSeconds);

            if (r_mapping.IsInsignificant(t, cand.SignificantLevel))
            {
                var edge = r_mapping.FindDip(t, side, MoveDirection.Inward, new DipSearch
                {
                    MinJump = TimeSpan.MaxValue,
                    StopAtSpeechEdge = true,
                    SignificantLevel = cand.SignificantLevel,
                    Inclusive = true,
                    Limit = other
                });
                if (edge.HasValue)
                    return edge.Value;

                // No significant speech found: in silence, at least move to the first mountain
                if (r_mapping.IsSilent(t))
                {
                    var dip = r_mapping.FindDip(t, side, MoveDirection.Inward, new DipSearch { Inclusive = true, Limit = other });
                    if (dip.HasValue && (dip.Value - t).Duration() <= maxDrift)
                        return dip.Value;
                }
                return t;
            }

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

            // Still too short (no more dips within MaxDrift): pad the end
            if (end - start < minDuration)
                end = start + minDuration;
        }

        #endregion

        #region Search: results -> judgement -> next window

        private void ProcessBatchResults(List<TimingCandidate> batch, Transcription fakeTranscription)
        {
            var touched = new List<TimingCandidate>();

            foreach (var fakeItem in fakeTranscription.Items)
            {
                var cand = batch.FirstOrDefault(c => c.Plan != null && c.Plan.Start == fakeItem.StartTime && c.Plan.End == fakeItem.EndTime);
                if (cand == null) continue;

                string heard = fakeItem.Metadata.TryGetValue(this.InnerOptions.MetadataAlwaysProduced, out var h) ? h : string.Empty;

                var plan = cand.Plan;
                var parent = GetTest(cand, plan.ParentIndex);
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
                DetectSuspiciousAnswer(cand, parent, test);

#if DEBUG
                _writer.WriteLine($"Original: {cand.OriginalText}");
                _writer.WriteLine($"Heard:    {heard}");
                _writer.WriteLine($"{cand.OriginalStartTime} => {test.Match.ToTraceString()}");
#endif

                // Trace: +/- shown relative to the window this one was derived from (the original timing for Init)
                char startMod = TimingCandidate.GetStartMod(parent?.Start ?? cand.OriginalStartTime, test.Start);
                char endMod = TimingCandidate.GetEndMod(parent?.End ?? cand.OriginalEndTime, test.End);

                cand.Tests.Add(test);
                cand.Plan = null;

                string stepName = cand.Tests.Count == 1 ? "Init" : $"#{cand.Tests.Count - 1}";
                string result = test.Match.ToTraceString()
                    + (test.SuspiciousStart ? " !S" : "")
                    + (test.SuspiciousEnd ? " !E" : "");
                cand.AddTraceRow(stepName, startMod, test.Start, test.End, endMod, result, heard);

                if (!touched.Contains(cand))
                    touched.Add(cand);
            }

            foreach (var cand in touched)
            {
                AdvanceCandidate(cand);
            }
        }

        private static TestedWindow GetTest(TimingCandidate cand, int index)
        {
            return index >= 0 && index < cand.Tests.Count ? cand.Tests[index] : null;
        }

        /// <summary>True when 'a' is further inside the clip than 'b' for that side.</summary>
        private static bool IsMoreInward(BoundarySide side, TimeSpan a, TimeSpan b)
        {
            return side == BoundarySide.Start ? a > b : a < b;
        }

        /// <summary>Audio removed on that side between the parent window and the test window (false when nothing was removed).</summary>
        private static bool TryGetRemovedRange(TimeWindow parent, TimeWindow test, BoundarySide side, out TimeSpan from, out TimeSpan to)
        {
            if (side == BoundarySide.Start)
            {
                from = parent.Start;
                to = test.Start;
            }
            else
            {
                from = test.End;
                to = parent.End;
            }
            return to > from;
        }

        private bool RemovedSignificantAudio(TimingCandidate cand, TimeWindow parent, TimeWindow test, BoundarySide side)
        {
            return TryGetRemovedRange(parent, test, side, out var from, out var to)
                && r_mapping.GetPeakLevel(from, to) >= cand.SignificantLevel;
        }

        /// <summary>
        /// Flags answers that claim syllables were lost on a side when that is not physically plausible:
        /// nothing was removed on that side, the removed audio has no significant energy, or the removed
        /// audio is too short to hold that many syllables (e.g. 7 syllables lost by removing 16 ms).
        /// </summary>
        private void DetectSuspiciousAnswer(TimingCandidate cand, TestedWindow parent, TestedWindow test)
        {
            if (parent == null)
                return;

            if (test.Match.IsMismatch)
            {
                if (!parent.Match.IsMismatch
                    && !RemovedSignificantAudio(cand, parent, test, BoundarySide.Start)
                    && !RemovedSignificantAudio(cand, parent, test, BoundarySide.End))
                {
                    test.SuspiciousStart = true;
                    test.SuspiciousEnd = true;
                }
                return;
            }

            test.SuspiciousStart = IsImplausibleLoss(cand, parent, test, BoundarySide.Start);
            test.SuspiciousEnd = IsImplausibleLoss(cand, parent, test, BoundarySide.End);
        }

        private bool IsImplausibleLoss(TimingCandidate cand, TestedWindow parent, TestedWindow test, BoundarySide side)
        {
            if (test.Match.Covers(side) || parent.Match.IsMismatch)
                return false;

            int newlyMissing = test.Match.Missing(side) - parent.Match.Missing(side);
            if (newlyMissing <= 0)
                return false;

            if (!TryGetRemovedRange(parent, test, side, out var from, out var to))
                return true; // nothing removed on that side, yet syllables were lost

            if (r_mapping.GetPeakLevel(from, to) < cand.SignificantLevel)
                return true; // only noise was removed

            double plausibleSyllables = 1 + (to - from).TotalSeconds / MinSyllableDurationSeconds;
            return newlyMissing > plausibleSyllables;
        }

        /// <summary>
        /// Called when the planned window has an answer.
        /// Judges each side independently, updates the best window and plans the next round.
        /// </summary>
        private void AdvanceCandidate(TimingCandidate cand)
        {
            var test = cand.Tests.Last();
            var parent = GetTest(cand, test.ParentIndex);
            cand.RetryCount++;

            // A test that heard something much further from the original than the best test does not prove coverage
            double bestSimilarity = cand.Tests
                .Where(t => !t.IsAnySuspicious)
                .Select(t => t.Match.Similarity)
                .DefaultIfEmpty(0)
                .Max();
            bool degraded = test.Match.Similarity < bestSimilarity - MaxSimilarityDrop;

            JudgeSide(cand, test, parent, BoundarySide.Start, degraded);
            JudgeSide(cand, test, parent, BoundarySide.End, degraded);

            UpdateCurrentBest(cand);

            cand.Plan = null;
            if (!cand.IsFullyLocked && cand.RetryCount < MaxRetriesPerNode)
                PlanNextWindow(cand, test, parent);

            if (cand.Plan == null)
            {
                cand.LockAll();
                SetFinalNotes(cand);
            }
        }

        /// <summary>Updates Good / Bad / lock of one side from the latest test.</summary>
        private void JudgeSide(TimingCandidate cand, TestedWindow test, TestedWindow parent, BoundarySide side, bool degraded)
        {
            var s = cand.Side(side);
            if (s.IsLocked)
                return;

            var boundary = test.Boundary(side);
            bool movedInward = parent != null && IsMoreInward(side, boundary, parent.Boundary(side));
            bool movedOutward = parent != null && IsMoreInward(side, parent.Boundary(side), boundary);

            // Suspicious: ignore the verdict, keep going, unless the AI keeps doing it
            if (test.IsSuspicious(side))
            {
                s.SuspiciousStreak++;
                if (s.SuspiciousStreak >= MaxSuspiciousPerSide)
                {
                    s.IsLocked = true;
                    s.LockedBySuspicion = true;
                }
                return;
            }
            s.SuspiciousStreak = 0;

            if (test.Match.IsMismatch || degraded)
            {
                if (movedInward)
                    SetBad(s, side, boundary);
                else if (parent != null && test.Match.IsMismatch && !movedOutward)
                    s.IsLocked = true; // that side did not move: the mismatch tells nothing more about it
                return;
            }

            if (test.Match.Covers(side))
            {
                SetGood(s, side, boundary);
                return;
            }

            // Missing on that side
            SetBad(s, side, boundary);
            if (movedOutward && s.Good == null)
            {
                bool gain = test.Match.Missing(side) < parent.Match.Missing(side)
                    || test.Match.Similarity > parent.Match.Similarity + SimilarityTieTolerance;
                if (!gain)
                    s.IsLocked = true; // expanding further doesn't help
            }
        }

        private static void SetGood(SideSearch s, BoundarySide side, TimeSpan boundary)
        {
            if (s.Good == null || IsMoreInward(side, boundary, s.Good.Value))
                s.Good = boundary;

            // A covering boundary at or inside the "bad" one: the bad answer was noise
            if (s.Bad.HasValue && !IsMoreInward(side, s.Bad.Value, s.Good.Value))
                s.Bad = null;
        }

        private static void SetBad(SideSearch s, BoundarySide side, TimeSpan boundary)
        {
            // Contradicts a known good boundary: ignore
            if (s.Good.HasValue && !IsMoreInward(side, boundary, s.Good.Value))
                return;

            // Keep the outermost bad boundary (the closest to Good)
            if (s.Bad == null || IsMoreInward(side, s.Bad.Value, boundary))
                s.Bad = boundary;
        }

        private void PlanNextWindow(TimingCandidate cand, TestedWindow test, TestedWindow parent)
        {
            var nextStart = PlanSide(cand, test, parent, BoundarySide.Start);
            var nextEnd = PlanSide(cand, test, parent, BoundarySide.End);

            if (cand.IsFullyLocked || nextEnd <= nextStart)
                return;

            // If this exact window was already tested, dead end -> stop
            if (cand.FindTest(nextStart, nextEnd) != null)
                return;

            cand.Plan = new TimeWindow(nextStart, nextEnd, TimingMove.Refine, cand.Tests.Count - 1);
        }

        /// <summary>Next position of one side. Locks the side when there is nowhere useful to go.</summary>
        private TimeSpan PlanSide(TimingCandidate cand, TestedWindow test, TestedWindow parent, BoundarySide side)
        {
            var s = cand.Side(side);
            if (s.IsLocked)
                return LockedBoundary(cand, side);

            var current = test.Boundary(side);
            TimeSpan? next;

            if (s.Good.HasValue && s.Bad.HasValue)
            {
                // Bracketed: probe between the good and the bad boundary
                next = Bisect(side, s.Good.Value, s.Bad.Value);
            }
            else if (test.IsSuspicious(side) && parent != null)
            {
                // Verdict ignored: continue in the same direction as the move that produced it
                var dir = IsMoreInward(side, parent.Boundary(side), current) ? MoveDirection.Outward : MoveDirection.Inward;
                next = Jump(cand, test, side, dir, current);
            }
            else if (s.Good.HasValue)
            {
                next = Jump(cand, test, side, MoveDirection.Inward, s.Good.Value);
            }
            else if (s.Bad.HasValue)
            {
                next = Jump(cand, test, side, MoveDirection.Outward, s.Bad.Value);
            }
            else
            {
                // Nothing known yet (Init mismatch): expand
                next = Jump(cand, test, side, MoveDirection.Outward, current);
            }

            if (next == null)
            {
                s.IsLocked = true;
                return LockedBoundary(cand, side);
            }
            return next.Value;
        }

        /// <summary>One move: at least MinJump, or exactly on a speech edge; within MaxDrift / minimum duration.</summary>
        private TimeSpan? Jump(TimingCandidate cand, TestedWindow test, BoundarySide side, MoveDirection dir, TimeSpan from)
        {
            var minDuration = TimeSpan.FromSeconds(MinSubtitleDurationSeconds);
            var maxDrift = TimeSpan.FromSeconds(MaxDriftSeconds);

            TimeSpan limit;
            if (dir == MoveDirection.Inward)
                limit = side == BoundarySide.Start ? test.End - minDuration : test.Start + minDuration;
            else
                limit = side == BoundarySide.Start ? cand.OriginalStartTime - maxDrift : cand.OriginalEndTime + maxDrift;

            return r_mapping.FindDip(from, side, dir, new DipSearch
            {
                MinJump = TimeSpan.FromSeconds(MinJumpSeconds),
                StopAtSpeechEdge = true,
                SignificantLevel = cand.SignificantLevel,
                Limit = limit
            });
        }

        /// <summary>Dip nearest to the middle of the bracket, at least BisectMinGap away from both ends (null if none).</summary>
        private TimeSpan? Bisect(BoundarySide side, TimeSpan good, TimeSpan bad)
        {
            var gap = TimeSpan.FromSeconds(BisectMinGapSeconds);
            var lo = good < bad ? good : bad;
            var hi = good < bad ? bad : good;
            var mid = lo + TimeSpan.FromTicks((hi - lo).Ticks / 2);

            return r_mapping.GetDips(side, lo + gap, hi - gap)
                .OrderBy(d => (d - mid).Duration())
                .Select(d => (TimeSpan?)d)
                .FirstOrDefault();
        }

        private TimeSpan LockedBoundary(TimingCandidate cand, BoundarySide side)
        {
            return cand.Side(side).Good ?? FallbackBest(cand).Boundary(side);
        }

        /// <summary>When a side has no good boundary: the most trustworthy test (non suspicious, covering, closest text, longest).</summary>
        private static TestedWindow FallbackBest(TimingCandidate cand)
        {
            var pool = cand.Tests.Where(t => !t.IsAnySuspicious).ToList();
            if (pool.Count == 0)
                pool = cand.Tests;

            return pool
                .OrderByDescending(t => t.Match.CoversAll)
                .ThenByDescending(t => t.Match.Similarity)
                .ThenByDescending(t => t.Duration)
                .First();
        }

        /// <summary>Current timing = per-side good boundaries (fallback: most trustworthy test).</summary>
        private void UpdateCurrentBest(TimingCandidate cand)
        {
            var fallback = FallbackBest(cand);
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
                var best = FallbackBest(cand);
                cand.AddValidationNote(string.IsNullOrWhiteSpace(best.HeardText)
                    ? "Unsure: Audio Inaudible"
                    : $"Unsure: Text Mismatch: {best.HeardText}");
                return;
            }

            if (cand.Start.Good == null)
                cand.AddValidationNote("Unsure: Missing Start");
            if (cand.End.Good == null)
                cand.AddValidationNote("Unsure: Missing End");
            if (cand.Start.LockedBySuspicion)
                cand.AddValidationNote("Unsure: Suspicious AI answers (Start)");
            if (cand.End.LockedBySuspicion)
                cand.AddValidationNote("Unsure: Suspicious AI answers (End)");
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
