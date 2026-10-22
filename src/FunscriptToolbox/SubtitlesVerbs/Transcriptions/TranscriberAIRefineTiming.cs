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
        }

        public class TimingCandidate
        {
            [JsonIgnore] public int Index { get; set; }

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

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Transcription transcription)
        {
            var jobState = transcription.CurrentJobState as JobState;
            if (jobState == null || true)
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

            context.WriteInfo($"[RefineTiming] Initializing Phase 1 (Valley-Seeking Padded Bounds) on {originalItems.Count} candidates...");
            var candidates = InitializeCandidates(originalItems, fullPcmAudio, jobState);

            var debugEnergyFile = new SubtitleFile();
            for (int i = 0; i < candidates.Count; i++)
            {
                var cand = candidates[i];
                debugEnergyFile.Subtitles.Add(new Subtitle(
                    cand.CurrentTestStart,
                    cand.CurrentTestEnd,
                    cand.Trace.ToArray(),
                    i + 1));
            }
            var debugEnergyPath = context.WIP.BaseFilePath.Replace("__", "_1_") + ".DEBUG-NEW.srt";
            debugEnergyFile.SaveSrt(debugEnergyPath);


            // Save state immediately after initialization
            context.WIP.Save();

            int totalAllowedTests = Math.Max(50, (int)(originalItems.Count * MaxBudgetRatio));

            while (jobState.TotalTestsRun < totalAllowedTests)
            {
                var pendingCandidates = candidates
                    .Where(c => !c.IsFullyLocked && c.RetryCount < MaxRetriesPerNode)
                    .ToList();

                if (pendingCandidates.Count == 0)
                    break;

                jobState.Iteration++;
                context.WriteInfo($"[RefineTiming] Iteration {jobState.Iteration}: Testing {pendingCandidates.Count} candidate timings (Tests run: {jobState.TotalTestsRun}/{totalAllowedTests})...");

                var batchToTest = SelectCandidatesForBatch(pendingCandidates);
                if (batchToTest.Count == 0)
                    break;

                var fakeTranscription = CreateFakeTranscription(transcription, batchToTest);

                var requestGenerator = ExecuteRequests(context, fakeTranscription);

                jobState.TotalTestsRun += batchToTest.Count;
                ProcessBatchResults(context, batchToTest, fakeTranscription, fullPcmAudio);

                context.WIP.Save();
            }

            FinalizeTranscription(context, transcription, candidates);

            transcription.MarkAsFinished();
            context.WIP.Save();
            context.WriteInfo($"[RefineTiming] Finished timing refinement. Processed {candidates.Count} nodes in {jobState.Iteration} iterations ({jobState.TotalTestsRun} tests total).");
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

                var cand = jobState.Candidates.FirstOrDefault(c => c.OriginalStartTime == item.StartTime);

                if (cand != null)
                {
                    // Re-link the ignored runtime properties
                    cand.Index = i;
                    cand.OriginalItem = item;
                    cand.CandidateText = item.Metadata.TryGetValue("CandidateText", out var ct) ? ct : string.Empty;
                    cand.HardMinStart = hardMinStart;
                    cand.HardMaxEnd = hardMaxEnd;
                }
                else
                {
                    // Phase 1: Inside-Out Valley Seeking
                    var (safeStart, safeEnd) = GetSafePaddedBounds(padStart, padEnd, item.StartTime, item.EndTime, fullPcmAudio);

                    cand = new TimingCandidate
                    {
                        Index = i,
                        OriginalStartTime = item.StartTime,
                        OriginalItem = item,
                        CandidateText = item.Metadata.TryGetValue("CandidateText", out var ctNew) ? ctNew : string.Empty,
                        HardMinStart = hardMinStart,
                        HardMaxEnd = hardMaxEnd,

                        CurrentTestStart = safeStart,
                        CurrentTestEnd = safeEnd,
                        LastGoodStart = safeStart,
                        LastGoodEnd = safeEnd,
                        HasSuccessfulY = false,
                        IsStartLocked = false,
                        IsEndLocked = false,
                        RetryCount = 0
                    };

                    cand.AddTrace($"Original: [{item.StartTime:mm\\:ss\\.fff} .. {item.EndTime:mm\\:ss\\.fff}]");

                    double startDiff = (safeStart - item.StartTime).TotalSeconds;
                    double endDiff = (safeEnd - item.EndTime).TotalSeconds;

                    if (Math.Abs(startDiff) > 0.005)
                        cand.AddTrace($"Phase 1 Padding: Start {(startDiff >= 0 ? "retract" : "expand")} {startDiff:F3}s");

                    if (Math.Abs(endDiff) > 0.005)
                        cand.AddTrace($"Phase 1 Padding: End {(endDiff >= 0 ? "expand" : "retract")} {endDiff:F3}s");

                    cand.AddTrace($"Test Bounds: [{cand.CurrentTestStart:mm\\:ss\\.fff} .. {cand.CurrentTestEnd:mm\\:ss\\.fff}]");

                    jobState.Candidates.Add(cand);
                }

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

            // 1. Calculate "Movement" (Max - Min) for a 150ms rolling window
            int windowRadius = 7; // +/- 7 frames = 15 frames = 150ms
            float[] movement = new float[rms.Length];

            for (int i = 0; i < rms.Length; i++)
            {
                int wStart = Math.Max(0, i - windowRadius);
                int wEnd = Math.Min(rms.Length - 1, i + windowRadius);
                float wMin = float.MaxValue;
                float wMax = float.MinValue;
                for (int k = wStart; k <= wEnd; k++)
                {
                    if (rms[k] < wMin) wMin = rms[k];
                    if (rms[k] > wMax) wMax = rms[k];
                }
                // Movement is the delta (variance) in this local window
                movement[i] = wMax - wMin;
            }

            // 2. Define "Stability" relative to the snippet's actual dynamic range
            float maxMovement = movement.Length > 0 ? movement.Max() : 0;
            float minMovement = movement.Length > 0 ? movement.Min() : 0;

            // "Stable" is any point that sits in the bottom 5% of this specific snippet's movement range.
            // This entirely replaces volume-based magic numbers. Loud hums will have low movement!
            float stableLimit = minMovement + (maxMovement - minMovement) * 0.05f;

            // -------------------------------------------------------------
            // EVALUATE START BOUNDARY
            // -------------------------------------------------------------
            int bestStartFrame = origStartFrame;
            bool startIsStable = movement[origStartFrame] <= stableLimit;

            if (startIsStable)
            {
                // Move RIGHT (inward) across the flatline until we hit the wall of movement
                for (int i = origStartFrame; i < origEndFrame; i++)
                {
                    if (movement[i] > stableLimit)
                    {
                        // The window sensed movement ahead. Step back by the window radius 
                        // to park exactly at the edge where stability ended.
                        bestStartFrame = Math.Max(origStartFrame, i - windowRadius);
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
                    if (movement[i] <= stableLimit)
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
            bool endIsStable = movement[origEndFrame] <= stableLimit;

            if (endIsStable)
            {
                // Move LEFT (inward) across the flatline until we hit the wall of movement
                for (int i = origEndFrame; i > bestStartFrame; i--)
                {
                    if (movement[i] > stableLimit)
                    {
                        // The window sensed movement behind. Step forward by the window radius 
                        // to park exactly at the edge where stability ended.
                        bestEndFrame = Math.Min(origEndFrame, i + windowRadius);
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
                    if (movement[i] <= stableLimit)
                    {
                        bestEndFrame = i;
                        break;
                    }
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
            var fakeItems = fakeTranscription.Items.ToList();

            for (int i = 0; i < batch.Count && i < fakeItems.Count; i++)
            {
                var cand = batch[i];
                var fakeItem = fakeItems[i];
                cand.RetryCount++;

                string status = fakeItem.Metadata.TryGetValue("Status", out var s) ? s?.Trim().ToUpperInvariant() : "Y";
                string heard = fakeItem.Metadata.TryGetValue("Heard", out var h) ? h : string.Empty;

                context.WriteInfo($"[RefineTiming] Node #{cand.Index} [{cand.CurrentTestStart:mm\\:ss\\.fff}..{cand.CurrentTestEnd:mm\\:ss\\.fff}] Status='{status}' Heard='{heard}'");
                cand.AddTrace($"Test #{cand.RetryCount}: AI=[{status}] Heard='{heard}'");

                if (status == "Y")
                {
                    cand.HasSuccessfulY = true;

                    // 1. Mark current bounds as safe
                    cand.LastGoodStart = cand.CurrentTestStart;
                    cand.LastGoodEnd = cand.CurrentTestEnd;

                    // 2. Aggressively shrink START (if not locked)
                    if (!cand.IsStartLocked)
                    {
                        var oldStart = cand.CurrentTestStart;
                        cand.CurrentTestStart = GetNextStartViaValley(cand.CurrentTestStart, fullPcmAudio, cand.HardMaxEnd);

                        if (cand.CurrentTestStart != oldStart)
                            cand.AddTrace($" -> Shrink Start (Valley): {oldStart:ss\\.fff} -> {cand.CurrentTestStart:ss\\.fff}");

                        if (cand.CurrentTestStart >= cand.CurrentTestEnd)
                        {
                            cand.IsStartLocked = true;
                            cand.AddTrace(" -> Start locked (Hit opposite boundary)");
                        }
                    }

                    // 3. Aggressively shrink END (if not locked)
                    if (!cand.IsEndLocked)
                    {
                        var oldEnd = cand.CurrentTestEnd;
                        cand.CurrentTestEnd = GetNextEndViaValley(cand.CurrentTestEnd, fullPcmAudio, cand.HardMinStart);

                        if (cand.CurrentTestEnd != oldEnd)
                            cand.AddTrace($" -> Shrink End (Valley): {oldEnd:ss\\.fff} -> {cand.CurrentTestEnd:ss\\.fff}");

                        if (cand.CurrentTestEnd <= cand.CurrentTestStart)
                        {
                            cand.IsEndLocked = true;
                            cand.AddTrace(" -> End locked (Hit opposite boundary)");
                        }
                    }
                }
                else
                {
                    // AI reported clipping! We shrank too far into the actual words.
                    bool startClipped = status.Contains("S");
                    bool endClipped = status.Contains("E");

                    if (startClipped)
                    {
                        cand.CurrentTestStart = cand.LastGoodStart;
                        cand.IsStartLocked = true;
                        cand.AddTrace($" -> Rolled back & Locked Start at {cand.LastGoodStart:mm\\:ss\\.fff}");
                    }
                    if (endClipped)
                    {
                        cand.CurrentTestEnd = cand.LastGoodEnd;
                        cand.IsEndLocked = true;
                        cand.AddTrace($" -> Rolled back & Locked End at {cand.LastGoodEnd:mm\\:ss\\.fff}");
                    }
                }

                // Failsafe: Prevent inversion
                if (cand.CurrentTestStart >= cand.CurrentTestEnd)
                {
                    cand.CurrentTestStart = cand.LastGoodStart;
                    cand.CurrentTestEnd = cand.LastGoodEnd;
                    cand.IsStartLocked = cand.IsEndLocked = true;
                    cand.AddTrace(" -> Failsafe: Reverted boundaries due to overlap.");
                }

                if (cand.IsFullyLocked || cand.RetryCount >= MaxRetriesPerNode)
                {
                    // Ensure final state rests on known good bounds
                    cand.CurrentTestStart = cand.LastGoodStart;
                    cand.CurrentTestEnd = cand.LastGoodEnd;
                    cand.AddTrace(" -> Candidate Finalized.");
                }
            }
        }

        private TimeSpan GetNextStartViaValley(TimeSpan currentStart, PcmAudio fullAudio, TimeSpan maxLimit)
        {
            TimeSpan proposed = currentStart + TimeSpan.FromSeconds(ShrinkStepSeconds);
            if (proposed >= maxLimit) return maxLimit;

            TimeSpan searchEnd = proposed + TimeSpan.FromSeconds(ValleySearchSeconds);
            if (searchEnd > maxLimit) searchEnd = maxLimit;

            return FindLowestRmsTime(fullAudio, proposed, searchEnd);
        }

        private TimeSpan GetNextEndViaValley(TimeSpan currentEnd, PcmAudio fullAudio, TimeSpan minLimit)
        {
            TimeSpan proposed = currentEnd - TimeSpan.FromSeconds(ShrinkStepSeconds);
            if (proposed <= minLimit) return minLimit;

            TimeSpan searchStart = proposed - TimeSpan.FromSeconds(ValleySearchSeconds);
            if (searchStart < minLimit) searchStart = minLimit;

            return FindLowestRmsTime(fullAudio, searchStart, proposed);
        }

        private TimeSpan FindLowestRmsTime(PcmAudio fullAudio, TimeSpan start, TimeSpan end)
        {
            if (start >= end) return start;

            var snippet = fullAudio.ExtractSnippet(start, end);
            float[] samples = ConvertPcmToFloat16K(snippet);
            var rms = GetRmsArray(samples);

            if (rms.Length == 0) return start;

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

            return start + TimeSpan.FromMilliseconds(minFrame * 10);
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

        private Transcription CreateFakeTranscription(Transcription template, List<TimingCandidate> batch)
        {
            var fake = new Transcription(
                id: template.Id + "_refine_fake2",
                metadataAlwaysProduced: this.Options.MetadataAlwaysProduced,
                privateMetadataNames: template.PrivateMetadataNames,
                language: template.Language,
                isFinished: false);

            foreach (var cand in batch)
            {
                var meta = new MetadataCollection();
                meta["CandidateText"] = cand.CandidateText;
                meta["CandidateIndex"] = cand.Index.ToString();

                fake.AddNewItem(cand.CurrentTestStart, cand.CurrentTestEnd, meta);
            }

            return fake;
        }

        private void FinalizeTranscription(
            SubtitleGeneratorContext context,
            Transcription transcription,
            List<TimingCandidate> candidates)
        {
            var newItems = new List<TranscribedItem>();

            foreach (var cand in candidates)
            {
                TimeSpan finalStart = cand.HasSuccessfulY ? cand.LastGoodStart : cand.OriginalItem.StartTime;
                TimeSpan finalEnd = cand.HasSuccessfulY ? cand.LastGoodEnd : cand.OriginalItem.EndTime;

                var meta = new MetadataCollection();
                foreach (var kvp in cand.OriginalItem.Metadata)
                {
                    meta[kvp.Key] = kvp.Value;
                }

                if (!cand.HasSuccessfulY)
                {
                    meta["Status"] = "Not cleaned";
                    meta["TimingTrace"] = string.Join(" | ", cand.Trace);
                    context.WriteInfo($"[RefineTiming] Node #{cand.Index} never verified as Y. Retaining original bounds.");
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
    }
}