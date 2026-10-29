using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner;
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
        public class JobState
        {
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

        [JsonProperty(Order = 20, Required = Required.Always)]
        public string SourceAudioIdForRefiner { get; set; }
        // Maximum number of rounds per candidate (a round tests 1 window).
        [JsonProperty(Order = 21)]

        public int MaxRetriesPerNode { get; set; } = 8;

        [JsonProperty(Order = 22)]
        public TimeSpan MaxDrift { get; set; } = TimeSpan.FromSeconds(1.2);
        [JsonProperty(Order = 23)]
        public TimeSpan MinSubtitleDuration { get; set; } = TimeSpan.FromSeconds(0.5);
        [JsonProperty(Order = 24)]
        public double MaxBudgetRatio { get; set; } = 8.0;
        [JsonProperty(Order = 25)]
        public float ValleyReach { get; set; } = 0.25f;
        [JsonProperty(Order = 26)]
        public bool IncludeTraceInOutputAlways { get; private set; } = false;
        [JsonProperty(Order = 27)]
        public bool IncludeTraceInOutputOnError { get; private set; } = true;
        [JsonProperty(Order = 28)]
        public bool IncludeTextInOutput { get; private set; } = true;


        [JsonProperty(Order = 34, Required = Required.Always)]
        public AIOptions InnerOptions { get; set; } = new AIOptions();


        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (context.WIP.AudioExtractions.Any(f => f.Id == this.SourceAudioIdForRefiner)
                && context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == this.SourceAudioIdForRefiner && f.IsFinished) == null)
            {
                reason = $"Audio extraction '{this.SourceAudioIdForRefiner}' is not done yet.";
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
            var jobState = transcription.CurrentJobState as JobState;
            if (jobState == null)
            {
                jobState = new JobState();
                transcription.CurrentJobState = jobState;
            }

            var fullPcmAudioForRefiner = context.WIP.AudioExtractions.First(f => f.Id == this.SourceAudioIdForRefiner && f.IsFinished).PcmAudio;
            var refiner = new TimingRefiner(
                fullPcmAudioForRefiner,                 
                maxRetriesPerNode: this.MaxRetriesPerNode,
                maxDrift: this.MaxDrift,
                minSubtitleDuration: this.MinSubtitleDuration,
                valleyReach: this.ValleyReach);
            context.WriteInfo($"  Preparation: Terrain mapping has {refiner.TerrainMapping.Segments.Count(f => !f.IsSilence)} possible speech segments, {refiner.TerrainMapping.Segments.Count(f => f.IsSilence)} silences.");
            DoExportSrt(context, transcription.Id, refiner.TerrainMapping.ToSegmentSubtitles(), "TERRAIN-MAP");

            var aggregation = this.Metadatas?.Aggregate(context);
            var originalItems = (aggregation.ReferenceTimingsWithMetadata ?? Array.Empty<TimedItemWithMetadata>())
                .OrderBy(f => f.StartTime)
                .ToList();

            context.WriteInfo($"  Preparation: Initializing acoustic bounds on {originalItems.Count} candidates...");
            foreach (var item in originalItems)
            {
                item.Metadata.TryGetValue(this.Options.MetadataNeeded, out var originalText);
                refiner.AddCandidate(item, originalText ?? "");
            }

            var iteration = 0;
            int totalTestsRun = 0;
            int totalAllowedTests = (int)(originalItems.Count * MaxBudgetRatio);
            while (totalTestsRun < totalAllowedTests && iteration < MaxRetriesPerNode + 1)
            {
                var candidatesToTest = refiner.Candidates
                    .Where(c => !c.IsFullyLocked && c.RetryCount < MaxRetriesPerNode)
                    .ToList();

                iteration++;
                int startLocked = refiner.Candidates.Count(c => c.Start.IsLocked);
                int endLocked = refiner.Candidates.Count(c => c.End.IsLocked);
                var isEndSoftLocked = startLocked == refiner.Candidates.Count ? string.Empty : "soft ";

                context.WriteInfo($"  Iteration {iteration}: {startLocked}/{refiner.Candidates.Count} start locked, {endLocked}/{refiner.Candidates.Count} end {isEndSoftLocked}locked, testing {candidatesToTest.Count} candidates, total allowed tests left: {totalAllowedTests - totalTestsRun}...");

                if (candidatesToTest.Count == 0)
                    break;

                var currentBatchTranscription = new Transcription(
                    id: $"{transcription.Id}_refine_iter_{iteration}",
                    metadataAlwaysProduced: this.InnerOptions.MetadataAlwaysProduced,
                    privateMetadataNames: null,
                    language: transcription.Language,
                    isFinished: false);

                var itemsToBeProcessed = new List<TimedItemWithMetadata>();
                foreach (var candidate in candidatesToTest)
                {
                    if (candidate.Plan != null && (totalTestsRun < totalAllowedTests))
                    {
                        var start = candidate.Plan.Start;
                        var end = candidate.Plan.End;
                        if (jobState.TryGetCachedResult(start, end, out var result))
                        {
                            // Add cached result in transcription
                            currentBatchTranscription.AddNewItem(start, end, MetadataCollection.CreateSimple(this.InnerOptions.MetadataAlwaysProduced, result));
                        }
                        else
                        {
                            // Add to the items that will be sent to AI
                            itemsToBeProcessed.Add(new TimedItemWithMetadata(start, end, new MetadataCollection()));
                        }
                        totalTestsRun++;
                    }
                }

                try
                {
                    var requestGenerator = ExecuteRequests(context, currentBatchTranscription, this.InnerOptions, itemsToBeProcessed.ToArray());
                    if (!requestGenerator.IsFinished())
                        return; // Interrupted
                }
                finally
                {
                    transcription.Costs.AddRange(currentBatchTranscription.Costs);
                    UpdateTranscription(transcription, refiner.Candidates);

                    // Make sure to cache all results, even if an exception occur
                    foreach (var item in currentBatchTranscription.Items)
                    {
                        jobState.AddCachedResult(item.StartTime, item.EndTime, item.Metadata[this.InnerOptions.MetadataAlwaysProduced]);
                    }
                    context.WIP.Save();
                }

                refiner.ProcessBatchResults(currentBatchTranscription.Items, this.InnerOptions.MetadataAlwaysProduced);
                UpdateTranscription(transcription, refiner.Candidates);

                DoExportMetatadaSrt(context, transcription, false);
            }

            refiner.FinalizeUndecidedOverlaps(refiner.Candidates);
            UpdateTranscription(transcription, refiner.Candidates);
            PrintSummary(context, transcription, refiner, totalTestsRun, totalAllowedTests);
            transcription.MarkAsFinished();
            context.WIP.Save();
        }

        private void UpdateTranscription(
            Transcription transcription,
            List<TimingCandidate> candidates)
        {
            var newItems = new List<TranscribedItem>();
            foreach (var candidate in candidates)
            {
                TimeSpan finalStart = candidate.CurrentTestStart;
                TimeSpan finalEnd = candidate.CurrentTestEnd;

                var meta = new MetadataCollection();
                foreach (var kvp in candidate.OriginalItem.Metadata)
                    meta[kvp.Key] = kvp.Value;

                var sb = new StringBuilder();
                if (candidate.ValidationNotes.Count > 0 || !candidate.IsFullyLocked)
                {
                    sb.AppendLine("[TIMING]");
                    if (!candidate.IsFullyLocked)
                        sb.AppendLine("Unsure: Unfinished");
                    foreach (var note in candidate.ValidationNotes)
                        sb.AppendLine("  " + note);
                }
                if (this.IncludeTraceInOutputAlways || (this.IncludeTraceInOutputOnError && sb.Length > 0))
                {
                    sb.AppendLine("[TRACES]");
                    foreach (var trace in candidate.Traces)
                    {
                        sb.AppendLine($"  {trace}");
                    }
                }
                if (this.IncludeTextInOutput)
                {
                    if (sb.Length > 0)
                        sb.AppendLine();
                    sb.AppendLine(candidate.OriginalText);
                }

                meta[this.Options.MetadataAlwaysProduced] = sb.ToString();
                meta["TimingTrace"] = string.Join("\n", candidate.Traces);

                var refinedItem = new TranscribedItem(
                    finalStart,
                    finalEnd,
                    meta,
                    (candidate.OriginalItem as TranscribedItem)?.NoSpeechProbability ?? 0.0,
                    (candidate.OriginalItem as TranscribedItem)?.Words);

                newItems.Add(refinedItem);
            }

            transcription.Items.Clear();
            transcription.Items.AddRange(newItems);
        }

        private void DoExportSrt(
            SubtitleGeneratorContext context,
            string transcriptionId,
            IEnumerable<Subtitle> subtitles,
            string name)
        {
            if (this.ExportMetadataSrt)
            {
                var extension = $".Worker.{transcriptionId}.{name}.srt";
                var virtualSubtitleFile = context.WIP.CreateVirtualSubtitleFile();
                virtualSubtitleFile.Subtitles.AddRange(subtitles);
                virtualSubtitleFile.Save(context.WIP.ParentPath, extension, context.SoftDelete);
            }
        }
        private void PrintSummary(
            SubtitleGeneratorContext context,
            Transcription transcription,
            TimingRefiner refiner,
            int totalTestsRun,
            int totalAllowedTests)
        {
            var candidates = refiner?.Candidates;
            if (candidates == null || candidates.Count == 0)
                return;

            int total = candidates.Count;
            int cleanCount = 0;
            int extraCount = 0;
            int reviewCount = 0;

            int missingStartCount = 0;
            int missingEndCount = 0;
            int mismatchCount = 0;
            int inaudibleCount = 0;
            int sharedZoneCount = 0;
            int unfinishedCount = 0;

            foreach (var cand in candidates)
            {
                bool hasNotes = cand.ValidationNotes.Count > 0;
                bool isUnfinished = !cand.IsFullyLocked;

                if (hasNotes || isUnfinished)
                {
                    reviewCount++;
                    if (isUnfinished)
                        unfinishedCount++;
                    if (cand.ValidationNotes.Any(n => n.IndexOf("Missing Start", StringComparison.OrdinalIgnoreCase) >= 0))
                        missingStartCount++;
                    if (cand.ValidationNotes.Any(n => n.IndexOf("Missing End", StringComparison.OrdinalIgnoreCase) >= 0))
                        missingEndCount++;
                    if (cand.ValidationNotes.Any(n => n.IndexOf("Mismatch", StringComparison.OrdinalIgnoreCase) >= 0))
                        mismatchCount++;
                    if (cand.ValidationNotes.Any(n => n.IndexOf("Inaudible", StringComparison.OrdinalIgnoreCase) >= 0))
                        inaudibleCount++;
                    if (cand.ValidationNotes.Any(n => n.IndexOf("Shared", StringComparison.OrdinalIgnoreCase) >= 0))
                        sharedZoneCount++;
                }
                else if (cand.Start.GoodState == SideState.Extra || cand.End.GoodState == SideState.Extra)
                {
                    extraCount++;
                }
                else
                {
                    cleanCount++;
                }
            }

            int resolvedOnInit = candidates.Count(c => c.Tests.Count <= 1);
            int refinedWithRetries = total - resolvedOnInit;

            double avgOrigDur = candidates.Average(c => (c.OriginalEndTime - c.OriginalStartTime).TotalSeconds);
            double avgFinalDur = candidates.Average(c => (c.CurrentTestEnd - c.CurrentTestStart).TotalSeconds);

            double avgStartDrift = candidates.Average(c => (c.CurrentTestStart - c.OriginalStartTime).TotalSeconds);
            double maxOutwardStartDrift = candidates.Min(c => (c.CurrentTestStart - c.OriginalStartTime).TotalSeconds);

            double avgEndDrift = candidates.Average(c => (c.CurrentTestEnd - c.OriginalEndTime).TotalSeconds);
            double maxOutwardEndDrift = candidates.Max(c => (c.CurrentTestEnd - c.OriginalEndTime).TotalSeconds);

            int overlaps = 0;
            int squashed = 0;
            var minViableDuration = TimeSpan.FromSeconds(0.05);
            for (int i = 0; i < total; i++)
            {
                if (candidates[i].CurrentTestEnd - candidates[i].CurrentTestStart <= minViableDuration)
                    squashed++;
                if (i < total - 1 && candidates[i].CurrentTestEnd > candidates[i + 1].CurrentTestStart)
                    overlaps++;
            }

            context.WriteInfo("================================================================================");
            context.WriteInfo($"  TIMING REFINER SUMMARY ({transcription.Id})");
            context.WriteInfo("================================================================================");
            context.WriteInfo($"  Total Candidates: {total} | Total Tests Run: {totalTestsRun} (Budget: {totalAllowedTests}, {((double)totalTestsRun / Math.Max(1, total)):0.00} tests/node)");
            context.WriteInfo("");
            context.WriteInfo("  QUALITY BREAKDOWN:");
            context.WriteInfo($"    [OK]   Clean / Full Coverage:       {cleanCount,4}  ({(cleanCount * 100.0 / total):0.0}%)  Both sides Ok, no warnings");
            if (extraCount > 0)
                context.WriteInfo($"    [OK]   Acceptable (Boundary Extra): {extraCount,4}  ({(extraCount * 100.0 / total):0.0}%)  Edge speech clamped cleanly");
            context.WriteInfo($"    [WARN] Review Needed:               {reviewCount,4}  ({(reviewCount * 100.0 / total):0.0}%)");
            if (missingStartCount > 0)
                context.WriteInfo($"           - Missing Start (drift limit): {missingStartCount,4}");
            if (missingEndCount > 0)
                context.WriteInfo($"           - Missing End (drift limit):   {missingEndCount,4}");
            if (mismatchCount > 0)
                context.WriteInfo($"           - Text Mismatch:               {mismatchCount,4}");
            if (inaudibleCount > 0)
                context.WriteInfo($"           - Audio Inaudible:             {inaudibleCount,4}");
            if (sharedZoneCount > 0)
                context.WriteInfo($"           - Shared Zone (Reverted):      {sharedZoneCount,4}");
            if (unfinishedCount > 0)
                context.WriteInfo($"           - Unfinished:                  {unfinishedCount,4}");
            context.WriteInfo("");
            context.WriteInfo("  TIMING ADJUSTMENTS & ACCURACY:");
            context.WriteInfo($"    - Resolved on Init (Round 0):       {resolvedOnInit,4}  ({(resolvedOnInit * 100.0 / total):0.0}%)");
            context.WriteInfo($"    - Refined with Retries (Round 1+):  {refinedWithRetries,4}  ({(refinedWithRetries * 100.0 / total):0.0}%)");
            context.WriteInfo($"    - Avg Duration:                     {avgOrigDur:0.00}s -> {avgFinalDur:0.00}s (diff: {(avgFinalDur - avgOrigDur):+0.00;-0.00;0.00}s)");
            context.WriteInfo($"    - Avg Start Drift:                  {avgStartDrift:+0.00;-0.00;0.00}s (max outward: {maxOutwardStartDrift:0.00}s)");
            context.WriteInfo($"    - Avg End Drift:                    {avgEndDrift:+0.00;-0.00;0.00}s (max outward: {maxOutwardEndDrift:+0.00;-0.00;0.00}s)");
            context.WriteInfo($"    - Overlaps / Squashed (<=0.05s):    {overlaps} / {squashed}");
            context.WriteInfo("================================================================================");
        }
    }
}
