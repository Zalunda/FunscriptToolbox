using FunscriptToolbox.Core;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberAIRefineTiming : TranscriberAI
    {
        public class JobState
        {
            public int Iteration { get; set; }
            public int TotalTestsRun { get; set; }

            public List<TimingCandidate> Candidates { get; set; } = new List<TimingCandidate>();

            // Tracks the inputs via their immutable primary key
            public List<TimeSpan> ActiveBatchStartTimes { get; set; }

            // Holds the active AI test batch so the framework can auto-save micro-progress
            public Transcription PendingBatch { get; set; }
        }

        public class TimingCandidate
        {
            public TimeSpan OriginalStartTime { get; set; }

            [JsonIgnore] public TimedItemWithMetadata OriginalItem { get; set; }
            [JsonIgnore] public string CandidateText { get; set; }
            [JsonIgnore] public TimeSpan HardMinStart { get; set; }
            [JsonIgnore] public TimeSpan HardMaxEnd { get; set; }

            public TimeSpan LastGoodStart { get; set; }
            public TimeSpan LastGoodEnd { get; set; }
            public TimeSpan CurrentTestStart { get; set; }
            public TimeSpan CurrentTestEnd { get; set; }

            public bool IsStartLocked { get; set; }
            public bool IsEndLocked { get; set; }

            public bool HasFoundGoodStart { get; set; }
            public bool HasFoundGoodEnd { get; set; }

            [JsonIgnore]
            public bool IsFullyLocked => IsStartLocked && IsEndLocked;

            public bool HasSuccessfulY { get; set; }
            public int RetryCount { get; set; }

            public List<string> Trace { get; set; } = new List<string>();

            public void AddTrace(string message) => Trace.Add(message);
        }

        [JsonProperty(Order = 43)]
        public int MaxRetriesPerNode { get; set; } = 4;

        [JsonProperty(Order = 44)]
        public double MaxBudgetRatio { get; set; } = 2.5;

        [JsonProperty(Order = 45)]
        public double MaxDriftSeconds { get; set; } = 0.5;

        [JsonProperty(Order = 46)]
        public double ShrinkStepSeconds { get; set; } = 0.2;

        [JsonProperty(Order = 47)]
        public double ValleySearchSeconds { get; set; } = 0.1;

        [JsonProperty(Order = 48)]
        public double MinSubtitleDurationSeconds { get; set; } = 0.5;

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Transcription transcription)
        {
            var jobState = transcription.CurrentJobState as JobState;
            if (jobState == null)
            {
                jobState = new JobState();
                transcription.CurrentJobState = jobState;
            }

            var audioExtractor = this.Options.BinaryDataExtractors.OfType<BinaryDataExtractorAudio>().FirstOrDefault();
            var fullPcmAudio = context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == audioExtractor?.SourceAudioId && f.IsFinished)?.PcmAudio;

            if (fullPcmAudio == null)
            {
                context.WriteError("Cannot refine timing: Full PCM audio extraction not found or not finished.");
                return;
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

            if (jobState.Candidates.Count == 0)
            {
                context.WriteInfo($"[RefineTiming] Initializing Phase 1 (Valley-Seeking Padded Bounds) on {originalItems.Count} candidates...");
                jobState.Candidates = InitializeCandidates(originalItems, fullPcmAudio, jobState);
                DoExportTraceSrt(context, transcription, jobState.Candidates, "FIRST-INIT");
                context.WIP.Save();
            }
            else
            {
                // Re-link ignored properties on resume
                for (int i = 0; i < originalItems.Count; i++)
                {
                    var item = originalItems[i];
                    var prevItem = i > 0 ? originalItems[i - 1] : null;
                    var nextItem = i < originalItems.Count - 1 ? originalItems[i + 1] : null;

                    var cand = jobState.Candidates.FirstOrDefault(c => c.OriginalStartTime == item.StartTime);
                    if (cand != null)
                    {
                        cand.OriginalItem = item;
                        cand.CandidateText = item.Metadata.TryGetValue("CandidateText", out var ct) ? ct : string.Empty;
                        cand.HardMinStart = prevItem != null ? TimeSpanExtensions.Min(prevItem.EndTime, item.StartTime) : TimeSpan.Zero;
                        cand.HardMaxEnd = nextItem != null ? TimeSpanExtensions.Max(nextItem.StartTime, item.EndTime) : TimeSpan.FromHours(10);
                    }
                }
            }

            DoExportTraceSrt(context, transcription, jobState.Candidates);
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
                    context.WriteInfo($"[RefineTiming] Iteration {jobState.Iteration}: Testing {pendingCandidates.Count} candidate timings (Tests run: {jobState.TotalTestsRun}/{totalAllowedTests})...");

                    batchToTest = SelectCandidatesForBatch(pendingCandidates);
                    if (batchToTest.Count == 0)
                        break;

                    // 1. Save the inputs using OriginalStartTime so we can reconstruct on crash
                    jobState.ActiveBatchStartTimes = batchToTest.Select(c => c.OriginalStartTime).ToList();

                    // 2. Create the EMPTY output container for the framework
                    string fakeId = $"{transcription.Id}_refine_iter_{jobState.Iteration}";
                    jobState.PendingBatch = new Transcription(
                        id: fakeId,
                        metadataAlwaysProduced: this.Options.MetadataAlwaysProduced,
                        privateMetadataNames: transcription.PrivateMetadataNames,
                        language: transcription.Language,
                        isFinished: false);

                    context.WIP.Save();
                }
                else
                {
                    context.WriteInfo($"[RefineTiming] Resuming interrupted Iteration {jobState.Iteration}...");

                    // Reconstruct the full input batch from the saved OriginalStartTimes
                    batchToTest = jobState.ActiveBatchStartTimes
                        .Select(origStart => jobState.Candidates.First(c => c.OriginalStartTime == origStart))
                        .ToList();
                }

                // 3. Build the input reference timings manually
                var referenceList = new List<TimedItemWithMetadata>();
                foreach (var cand in batchToTest)
                {
                    var meta = new MetadataCollection();
                    meta["CandidateText"] = cand.CandidateText;
                    referenceList.Add(new TimedItemWithMetadata(cand.CurrentTestStart, cand.CurrentTestEnd, meta));
                }

                var requestGenerator = new AIRequestGenerator(
                    referenceList.ToArray(),
                    jobState.PendingBatch,
                    transcription.Language,
                    transcription.Language,
                    this.Options);

                ExecuteRequests(context, jobState.PendingBatch, requestGenerator);

                if (!requestGenerator.IsFinished())
                {
                    // Job stopped short
                    return;
                }

                // We only increment TotalTests for items that actually got a response
                jobState.TotalTestsRun += jobState.PendingBatch.Items.Count;

                ProcessBatchResults(context, batchToTest, jobState.PendingBatch, fullPcmAudio);

                // Clean up state for the next iteration
                jobState.PendingBatch = null;
                jobState.ActiveBatchStartTimes = null;
                context.WIP.Save();

                DoExportTraceSrt(context, transcription, jobState.Candidates, "TRACE");

                int lockedNodes = jobState.Candidates.Count(c => c.IsFullyLocked);
                int lockedSides = jobState.Candidates.Sum(c => (c.IsStartLocked ? 1 : 0) + (c.IsEndLocked ? 1 : 0));
                context.WriteInfo($"[RefineTiming] Progress: {lockedNodes}/{jobState.Candidates.Count} nodes finished | {lockedSides}/{jobState.Candidates.Count * 2} boundary sides locked.");

            }

            FinalizeTranscription(context, transcription, jobState.Candidates);

            transcription.MarkAsFinished();
            context.WIP.Save();
            context.WriteInfo($"[RefineTiming] Finished timing refinement. Processed {jobState.Candidates.Count} nodes in {jobState.Iteration} iterations ({jobState.TotalTestsRun} tests total).");
        }

        private List<TimingCandidate> InitializeCandidates(List<TimedItemWithMetadata> originalItems, PcmAudio fullPcmAudio, JobState jobState)
        {
            var candidates = new List<TimingCandidate>();

            for (int i = 0; i < originalItems.Count; i++)
            {
                var item = originalItems[i];
                var prevItem = i > 0 ? originalItems[i - 1] : null;
                var nextItem = i < originalItems.Count - 1 ? originalItems[i + 1] : null;

                TimeSpan hardMinStart = prevItem != null ? TimeSpanExtensions.Min(prevItem.EndTime, item.StartTime) : TimeSpan.Zero;
                TimeSpan hardMaxEnd = nextItem != null ? TimeSpanExtensions.Max(nextItem.StartTime, item.EndTime) : TimeSpan.FromHours(10);

                TimeSpan padStart = TimeSpanExtensions.Max(hardMinStart, item.StartTime - TimeSpan.FromSeconds(MaxDriftSeconds));
                TimeSpan padEnd = TimeSpanExtensions.Min(hardMaxEnd, item.EndTime + TimeSpan.FromSeconds(MaxDriftSeconds));

                if (padEnd <= padStart)
                {
                    padStart = item.StartTime;
                    padEnd = item.EndTime;
                }

                var (safeStart, safeEnd) = GetSafePaddedBounds(padStart, padEnd, item.StartTime, item.EndTime, fullPcmAudio);

                var cand = new TimingCandidate
                {
                    OriginalStartTime = item.StartTime,
                    OriginalItem = item,
                    CandidateText = item.Metadata.TryGetValue("CandidateText", out var ct) ? ct : string.Empty,
                    HardMinStart = hardMinStart,
                    HardMaxEnd = hardMaxEnd,

                    CurrentTestStart = safeStart,
                    CurrentTestEnd = safeEnd,
                    LastGoodStart = safeStart,
                    LastGoodEnd = safeEnd,

                    HasFoundGoodStart = false,
                    HasFoundGoodEnd = false,
                    HasSuccessfulY = false,
                    IsStartLocked = false,
                    IsEndLocked = false,
                    RetryCount = 0
                };

                cand.AddTrace($"Original: [{item.StartTime:mm\\:ss\\.fff} .. {item.EndTime:mm\\:ss\\.fff}]");

                double startDiff = (safeStart - item.StartTime).TotalSeconds;
                double endDiff = (safeEnd - item.EndTime).TotalSeconds;

                if (Math.Abs(startDiff) > 0.005) 
                    cand.AddTrace($"Phase 1 Padding: {(startDiff >= 0 ? "Retract" : "Expand")} Start {startDiff:F3}s");
                if (Math.Abs(endDiff) > 0.005) 
                    cand.AddTrace($"Phase 1 Padding: {(endDiff >= 0 ? "Expand" : "Retract")} End {endDiff:F3}s");
                cand.AddTrace($"Test Bounds: [{cand.CurrentTestStart:mm\\:ss\\.fff} .. {cand.CurrentTestEnd:mm\\:ss\\.fff}]");

                candidates.Add(cand);
            }

            return candidates;
        }

        private (TimeSpan start, TimeSpan end) GetSafePaddedBounds(
                    TimeSpan padStart, TimeSpan padEnd,
                    TimeSpan origStart, TimeSpan origEnd,
                    PcmAudio fullAudio)
        {
            var snippet = fullAudio.ExtractSnippet(padStart, padEnd);
            float[] samples = ConvertPcmToFloat16K(snippet);
            var rms = GetRmsArray(samples);
            if (rms.Length == 0) return (origStart, origEnd);

            int origStartFrame = (int)((origStart - padStart).TotalMilliseconds / 10.0);
            int origEndFrame = (int)((origEnd - padStart).TotalMilliseconds / 10.0);
            origStartFrame = Math.Max(0, Math.Min(rms.Length - 1, origStartFrame));
            origEndFrame = Math.Max(0, Math.Min(rms.Length - 1, origEndFrame));

            // Helper for directional/symmetrical variance checks
            float GetVariance(int left, int right)
            {
                int start = Math.Max(0, left);
                int end = Math.Min(rms.Length - 1, right);
                if (start > end) return 0;
                float vMin = float.MaxValue;
                float vMax = float.MinValue;
                for (int k = start; k <= end; k++)
                {
                    if (rms[k] < vMin) vMin = rms[k];
                    if (rms[k] > vMax) vMax = rms[k];
                }
                return vMax - vMin;
            }

            // 1. Calculate dynamic stability limit based on the snippet's actual range
            float maxMovement = 0;
            float minMovement = float.MaxValue;
            for (int i = 0; i < rms.Length; i++)
            {
                float v = GetVariance(i - 7, i + 7);
                if (v > maxMovement) maxMovement = v;
                if (v < minMovement) minMovement = v;
            }
            if (minMovement == float.MaxValue) minMovement = 0;

            // Using 8% and a 0.002f floor to prevent runaway expansion on pure flatlines
            float stableLimit = Math.Max(minMovement + (maxMovement - minMovement) * 0.08f, 0.002f);

            // -------------------------------------------------------------
            // EVALUATE START BOUNDARY
            // -------------------------------------------------------------
            int bestStartFrame = origStartFrame;
            // Directional Check: Only look 70ms forward
            bool startIsStable = GetVariance(origStartFrame, origStartFrame + 7) <= stableLimit;

            if (startIsStable)
            {
                // Fast-forward jump 7 frames since we already verified they are stable
                int startIndex = Math.Min(origEndFrame, origStartFrame + 7);

                // Move RIGHT (inward) using the asymmetrical window
                for (int i = startIndex; i < origEndFrame; i++)
                {
                    // Check: 70ms tail behind us (i - 7), 30ms short bumper ahead of us (i + 5)
                    if (GetVariance(i - 7, i + 5) > stableLimit)
                    {
                        // The +3 bumper hit the speech mountain. We park exactly at 'i', 
                        // leaving a perfect 30ms gap before the sound starts.
                        bestStartFrame = i;
                        break;
                    }
                    bestStartFrame = i;
                }
            }
            else
            {
                // Move LEFT (outward) sliding down the slope to find a flatline or lowest valley
                float lowestRms = rms[origStartFrame];
                for (int i = origStartFrame; i >= 0; i--)
                {
                    if (rms[i] < lowestRms)
                    {
                        lowestRms = rms[i];
                        bestStartFrame = i;
                    }

                    // If we successfully found a stable flatline, stop wandering left
                    if (GetVariance(i - 7, i + 7) <= stableLimit)
                    {
                        bestStartFrame = i;
                        break;
                    }
                }
            }

            // -------------------------------------------------------------
            // EVALUATE END BOUNDARY
            // -------------------------------------------------------------
            int bestEndFrame = origEndFrame;
            // Directional Check: Only look 70ms backward
            bool endIsStable = GetVariance(origEndFrame - 7, origEndFrame) <= stableLimit;

            if (endIsStable)
            {
                // Fast-forward jump 7 frames since we already verified they are stable
                int startIndex = Math.Max(bestStartFrame, origEndFrame - 7);

                // Move LEFT (inward) using the asymmetrical window
                for (int i = startIndex; i > bestStartFrame; i--)
                {
                    // Check: 30ms short bumper ahead of us (i - 5), 70ms tail behind us (i + 7)
                    if (GetVariance(i - 5, i + 7) > stableLimit)
                    {
                        // The -3 bumper hit the speech mountain. We park exactly at 'i', 
                        // leaving a perfect 30ms trailing gap after the sound ends.
                        bestEndFrame = i;
                        break;
                    }
                    bestEndFrame = i;
                }
            }
            else
            {
                // Move RIGHT (outward) sliding down the slope to find a flatline or lowest valley
                float lowestRms = rms[origEndFrame];
                for (int i = origEndFrame; i < rms.Length; i++)
                {
                    if (rms[i] < lowestRms)
                    {
                        lowestRms = rms[i];
                        bestEndFrame = i;
                    }

                    // If we successfully found a stable flatline, stop wandering right
                    if (GetVariance(i - 7, i + 7) <= stableLimit)
                    {
                        bestEndFrame = i;
                        break;
                    }
                }
            }

            // Minimum Duration Safeguard
            int minFrames = (int)(MinSubtitleDurationSeconds * 1000 / 10.0);
            if (bestEndFrame - bestStartFrame < minFrames)
            {
                int midPoint = (bestStartFrame + bestEndFrame) / 2;
                int half = minFrames / 2;

                // Expand outward from the midpoint to guarantee the minimum duration
                bestStartFrame = Math.Max(0, midPoint - half);
                bestEndFrame = Math.Min(rms.Length - 1, bestStartFrame + minFrames);

                // If it hit the right wall, push the left side further back
                if (bestEndFrame - bestStartFrame < minFrames)
                {
                    bestStartFrame = Math.Max(0, bestEndFrame - minFrames);
                }
            }

            return (
                padStart + TimeSpan.FromMilliseconds(bestStartFrame * 10),
                padStart + TimeSpan.FromMilliseconds(bestEndFrame * 10)
            );
        }

        private void ProcessBatchResults(
                    SubtitleGeneratorContext context,
                    List<TimingCandidate> batch,
                    Transcription fakeTranscription,
                    PcmAudio fullPcmAudio)
        {
            foreach (var fakeItem in fakeTranscription.Items)
            {
                // 1. Safely find the matching candidate using the StartTime key
                var cand = batch.FirstOrDefault(c => c.CurrentTestStart == fakeItem.StartTime);
                if (cand == null) continue; // Ghost item from AI hallucination? Ignore it.

                // 2. Check if the AI actually returned data for this node
                if (!fakeItem.Metadata.TryGetValue("Status", out var s))
                {
                    context.WriteInfo($"[RefineTiming] Node #{cand.OriginalStartTime} [{cand.CurrentTestStart:mm\\:ss\\.fff}] received no Status from AI. Skipping to retry next iteration.");
                    continue;
                }

                cand.RetryCount++;

                string status = s.Trim().ToUpperInvariant();
                string heard = fakeItem.Metadata.TryGetValue("Heard", out var h) ? h : string.Empty;
                string audit = fakeItem.Metadata.TryGetValue("Audit", out var a) ? a : string.Empty;

                cand.AddTrace($"Test #{cand.RetryCount}: AI=[{status}] Heard='{heard}', Audio='{audit}'");

                bool startClipped = status.Contains("S");
                bool endClipped = status.Contains("E");
                if (status == "Y") cand.HasSuccessfulY = true;

                // These track what the NEXT test boundaries will be
                TimeSpan nextTestStart = cand.CurrentTestStart;
                TimeSpan nextTestEnd = cand.CurrentTestEnd;

                // -------------------------------------------------------------
                // 1. EVALUATE START
                // -------------------------------------------------------------
                if (startClipped)
                {
                    if (cand.HasFoundGoodStart)
                    {
                        nextTestStart = cand.LastGoodStart;
                        cand.IsStartLocked = true;
                        cand.AddTrace($" -> Locked Start at {cand.LastGoodStart:ss\\.fff} (Reverted)");
                    }
                    else
                    {
                        nextTestStart = ExpandBoundary(cand.CurrentTestStart, true, fullPcmAudio, cand.HardMinStart);
                        cand.AddTrace($" -> Expand Start: {cand.CurrentTestStart:ss\\.fff} -> {nextTestStart:ss\\.fff}");
                    }
                }
                else
                {
                    cand.HasFoundGoodStart = true;
                    cand.LastGoodStart = cand.CurrentTestStart;

                    if (!cand.IsStartLocked)
                    {
                        TimeSpan proposedStart = ShrinkBoundary(cand.CurrentTestStart, true, fullPcmAudio, cand.HardMaxEnd);
                        if (cand.CurrentTestEnd - proposedStart < TimeSpan.FromSeconds(MinSubtitleDurationSeconds))
                        {
                            nextTestStart = cand.LastGoodStart;
                            cand.IsStartLocked = true;
                            cand.AddTrace(" -> Start locked (Hit minimum duration limit)");
                        }
                        else
                        {
                            nextTestStart = proposedStart;
                            cand.AddTrace($" -> Shrink Start: {cand.CurrentTestStart:ss\\.fff} -> {nextTestStart:ss\\.fff}");
                        }
                    }
                }

                // -------------------------------------------------------------
                // 2. EVALUATE END
                // -------------------------------------------------------------
                if (endClipped)
                {
                    if (cand.HasFoundGoodEnd)
                    {
                        nextTestEnd = cand.LastGoodEnd;
                        cand.IsEndLocked = true;
                        cand.AddTrace($" -> Locked End at {cand.LastGoodEnd:ss\\.fff} (Reverted)");
                    }
                    else
                    {
                        nextTestEnd = ExpandBoundary(cand.CurrentTestEnd, false, fullPcmAudio, cand.HardMaxEnd);
                        cand.AddTrace($" -> Expand End: {cand.CurrentTestEnd:ss\\.fff} -> {nextTestEnd:ss\\.fff}");
                    }
                }
                else
                {
                    cand.HasFoundGoodEnd = true;
                    cand.LastGoodEnd = cand.CurrentTestEnd;

                    if (!cand.IsEndLocked)
                    {
                        TimeSpan proposedEnd = ShrinkBoundary(cand.CurrentTestEnd, false, fullPcmAudio, cand.HardMinStart);
                        if (proposedEnd - cand.CurrentTestStart < TimeSpan.FromSeconds(MinSubtitleDurationSeconds))
                        {
                            nextTestEnd = cand.LastGoodEnd;
                            cand.IsEndLocked = true;
                            cand.AddTrace(" -> End locked (Hit minimum duration limit)");
                        }
                        else
                        {
                            nextTestEnd = proposedEnd;
                            cand.AddTrace($" -> Shrink End: {cand.CurrentTestEnd:ss\\.fff} -> {nextTestEnd:ss\\.fff}");
                        }
                    }
                }

                // -------------------------------------------------------------
                // 3. APPLY AND CLAMP NEW BOUNDARIES
                // -------------------------------------------------------------
                if (nextTestStart >= nextTestEnd || (nextTestEnd - nextTestStart) < TimeSpan.FromSeconds(MinSubtitleDurationSeconds))
                {
                    // Failsafe overlap. Stick to the widest valid bounds.
                    cand.CurrentTestStart = cand.HasFoundGoodStart ? cand.LastGoodStart : cand.CurrentTestStart;
                    cand.CurrentTestEnd = cand.HasFoundGoodEnd ? cand.LastGoodEnd : cand.CurrentTestEnd;
                    cand.IsStartLocked = cand.IsEndLocked = true;
                    cand.AddTrace(" -> Failsafe: Locked boundaries due to overlap or minimum duration limit.");
                }
                else
                {
                    cand.CurrentTestStart = nextTestStart;
                    cand.CurrentTestEnd = nextTestEnd;
                }

                // If max retries hit, lock in the best known state to prevent infinite loops
                if (cand.IsFullyLocked || cand.RetryCount >= MaxRetriesPerNode)
                {
                    if (cand.HasFoundGoodStart) cand.CurrentTestStart = cand.LastGoodStart;
                    if (cand.HasFoundGoodEnd) cand.CurrentTestEnd = cand.LastGoodEnd;
                }
            }
        }

        /// <summary>
        /// Expands the boundary outward by AT LEAST ShrinkStepSeconds. 
        /// Uses variance to crawl over small speech bumps until a stable valley is reached.
        /// </summary>
        private TimeSpan ExpandBoundary(TimeSpan currentTime, bool isStart, PcmAudio fullAudio, TimeSpan hardLimit)
        {
            TimeSpan minJump = TimeSpan.FromSeconds(ShrinkStepSeconds);
            TimeSpan proposedTime = isStart ? currentTime - minJump : currentTime + minJump;

            if (isStart && proposedTime <= hardLimit) return hardLimit;
            if (!isStart && proposedTime >= hardLimit) return hardLimit;

            // Extract a larger window to analyze the terrain we are expanding into
            TimeSpan snippetStart = isStart ? proposedTime - TimeSpan.FromSeconds(1.0) : proposedTime - TimeSpan.FromSeconds(0.1);
            TimeSpan snippetEnd = isStart ? proposedTime + TimeSpan.FromSeconds(0.1) : proposedTime + TimeSpan.FromSeconds(1.0);

            if (snippetStart < TimeSpan.Zero) snippetStart = TimeSpan.Zero;

            var snippet = fullAudio.ExtractSnippet(snippetStart, snippetEnd);
            float[] samples = ConvertPcmToFloat16K(snippet);
            var rms = GetRmsArray(samples);
            if (rms.Length == 0) return proposedTime;

            float GetVariance(int left, int right)
            {
                int start = Math.Max(0, left);
                int end = Math.Min(rms.Length - 1, right);
                if (start > end) return 0;
                float vMin = float.MaxValue, vMax = float.MinValue;
                for (int k = start; k <= end; k++)
                {
                    if (rms[k] < vMin) vMin = rms[k];
                    if (rms[k] > vMax) vMax = rms[k];
                }
                return vMax - vMin;
            }

            float maxMovement = 0; float minMovement = float.MaxValue;
            for (int i = 0; i < rms.Length; i++)
            {
                float v = GetVariance(i - 7, i + 7);
                if (v > maxMovement) maxMovement = v;
                if (v < minMovement) minMovement = v;
            }
            if (minMovement == float.MaxValue) minMovement = 0;
            float stableLimit = Math.Max(minMovement + (maxMovement - minMovement) * 0.08f, 0.002f);

            int proposedFrame = (int)((proposedTime - snippetStart).TotalMilliseconds / 10.0);
            proposedFrame = Math.Max(0, Math.Min(rms.Length - 1, proposedFrame));

            int bestFrame = proposedFrame;

            if (isStart)
            {
                for (int i = proposedFrame; i >= 0; i--)
                {
                    if (GetVariance(i - 7, i + 7) <= stableLimit) 
                    { 
                        bestFrame = i; 
                        break; 
                    }
                    bestFrame = i;
                }
            }
            else
            {
                for (int i = proposedFrame; i < rms.Length; i++)
                {
                    if (GetVariance(i - 7, i + 7) <= stableLimit)
                    {
                        bestFrame = i;
                        break;
                    }
                    bestFrame = i;
                }
            }

            TimeSpan finalTime = snippetStart + TimeSpan.FromMilliseconds(bestFrame * 10);

            if (isStart && finalTime < hardLimit) return hardLimit;
            if (!isStart && finalTime > hardLimit) return hardLimit;

            return finalTime;
        }

        /// <summary>
        /// Shrinks the boundary inward by EXACTLY ShrinkStepSeconds, then looks forward 
        /// to snap into the lowest RMS valley. Guarantees a minimum step distance.
        /// </summary>
        private TimeSpan ShrinkBoundary(TimeSpan currentTime, bool isStart, PcmAudio fullAudio, TimeSpan hardLimit)
        {
            TimeSpan minStep = TimeSpan.FromSeconds(ShrinkStepSeconds);
            TimeSpan searchRadius = TimeSpan.FromSeconds(ValleySearchSeconds);

            TimeSpan snippetStart, snippetEnd;

            if (isStart)
            {
                // Must jump inward by AT LEAST minStep (e.g. 0.2s)
                snippetStart = currentTime + minStep;
                snippetEnd = snippetStart + searchRadius; // Look forward up to 0.1s to find a valley
                if (snippetStart > hardLimit) return hardLimit;
                if (snippetEnd > hardLimit) snippetEnd = hardLimit;
            }
            else
            {
                // Must jump inward by AT LEAST minStep (e.g. 0.2s)
                snippetEnd = currentTime - minStep;
                snippetStart = snippetEnd - searchRadius; // Look backward up to 0.1s to find a valley
                if (snippetEnd < hardLimit) return hardLimit;
                if (snippetStart < hardLimit) snippetStart = hardLimit;
            }

            var snippet = fullAudio.ExtractSnippet(snippetStart, snippetEnd);
            float[] samples = ConvertPcmToFloat16K(snippet);
            var rms = GetRmsArray(samples);
            if (rms.Length == 0) return isStart ? snippetStart : snippetEnd;

            int minFrame = 0;
            float minRms = float.MaxValue;
            for (int i = 0; i < rms.Length; i++)
            {
                if (rms[i] < minRms)
                {
                    minRms = rms[i];
                    minFrame = i;
                }
            }

            return snippetStart + TimeSpan.FromMilliseconds(minFrame * 10);
        }

        private float[] GetRmsArray(float[] samples, int frameSize = 160 /* 10ms at 16kHz */)
        {
            int n = samples.Length / frameSize;
            float[] rms = new float[n];
            for (int i = 0; i < n; i++)
            {
                float sumSq = 0;
                int offset = i * frameSize;
                for (int j = 0; j < frameSize; j++)
                {
                    sumSq += samples[offset + j] * samples[offset + j];
                }
                rms[i] = (float)Math.Sqrt(sumSq / frameSize);
            }
            return rms;
        }

        private float[] ConvertPcmToFloat16K(PcmAudio pcmAudio)
        {
            byte[] bytes = pcmAudio.Data;
            int numSamples = bytes.Length / 2;
            float[] samples = new float[numSamples];

            for (int i = 0; i < numSamples; i++)
            {
                short sample = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
                samples[i] = sample / 32768.0f;
            }

            return samples;
        }

        private List<TimingCandidate> SelectCandidatesForBatch(List<TimingCandidate> pendingCandidates)
        {
            var batch = new List<TimingCandidate>();
            TimingCandidate lastSelected = null;

            foreach (var cand in pendingCandidates)
            {
                if (lastSelected != null && (cand.OriginalItem.StartTime - lastSelected.OriginalItem.EndTime).TotalMilliseconds < 300)
                {
                    if (!lastSelected.IsFullyLocked)
                        continue;
                }

                batch.Add(cand);
                lastSelected = cand;
            }

            return batch;
        }

        private void FinalizeTranscription(
            SubtitleGeneratorContext context,
            Transcription transcription,
            List<TimingCandidate> candidates)
        {
            var newItems = new List<TranscribedItem>();

            foreach (var cand in candidates)
            {
                // Prefer LastGood limits if we found them.
                // If we didn't (because we kept expanding and ran out of tests), we DO NOT revert to the Original clipped bounds.
                // We keep the CurrentTest (expanded) bounds to guarantee the audio is fully covered!
                TimeSpan finalStart = cand.HasFoundGoodStart ? cand.LastGoodStart : cand.CurrentTestStart;
                TimeSpan finalEnd = cand.HasFoundGoodEnd ? cand.LastGoodEnd : cand.CurrentTestEnd;

                var meta = new MetadataCollection();
                foreach (var kvp in cand.OriginalItem.Metadata)
                {
                    meta[kvp.Key] = kvp.Value;
                }

                if (!cand.HasSuccessfulY)
                {
                    meta["Status"] = "Not cleaned";
                    meta["TimingTrace"] = string.Join(" | ", cand.Trace);
                    context.WriteInfo($"[RefineTiming] Node #{cand.OriginalStartTime} never verified as Y. Keeping widest tested bounds.");
                }
                else
                {
                    meta["Status"] = "Cleaned";
                    meta["TimingTrace"] = string.Join(" | ", cand.Trace);
                }

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

        private void DoExportTraceSrt(
            SubtitleGeneratorContext context,
            Transcription transcription,
            IEnumerable<TimingCandidate> candidates,
            string name)
        {
            if (this.ExportMetadataSrt)
            {
                var extension = $".Worker.{transcription.Id}.{name}.srt";

                var virtualSubtitleFile = context.WIP.CreateVirtualSubtitleFile();
                virtualSubtitleFile.Subtitles.AddRange(candidates.Select(cand => new Subtitle(cand.CurrentTestStart, cand.CurrentTestEnd, cand.Trace.ToArray())));
                virtualSubtitleFile.Save(context.WIP.ParentPath, extension, context.SoftDelete);

                var fullfilePath = context.WIP.BaseFilePath + extension;
                if (context.WIP.TimelineMap.IsMultipart)
                {
                    var newFullFile = new SubtitleFile();
                    newFullFile.Subtitles.AddRange(
                        virtualSubtitleFile.Subtitles.Select(item =>
                            new Subtitle(item.StartTime, item.EndTime, item.Text)));
                    newFullFile.SaveSrt(fullfilePath);
                }
            }
        }
    }
}