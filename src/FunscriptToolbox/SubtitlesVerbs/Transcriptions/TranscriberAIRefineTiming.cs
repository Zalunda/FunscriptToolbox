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
    /// Refines the timing of an existing transcription by generating multiple comparative audio clip options
    /// (Audio_Max_Max, Audio_M2_M2, Audio_M1_M1, Audio_Init_Init, Audio_P1_P1, Audio_P2_P2) per subtitle
    /// and asking the AI to select the best Start and End boundaries.
    /// </summary>
    public class TranscriberAIRefineTiming : TranscriberAI
    {
        public class CachedCandidateResponse
        {
            public string BestStart { get; set; }
            public string BestEnd { get; set; }
            public string HeardText { get; set; }
        }

        public class JobState
        {
            public Dictionary<string, CachedCandidateResponse> CachedResults = new Dictionary<string, CachedCandidateResponse>();

            public static string BuildKey(TimeSpan originalStart, TimingRound round)
            {
                var clipsSig = string.Join(";", round.Clips.Select(c => $"{c.StartId}-{c.EndId}:{c.Start:hh\\:mm\\:ss\\.fff}-{c.End:hh\\:mm\\:ss\\.fff}"));
                return $"{originalStart:hh\\:mm\\:ss\\.fff}|{round.CenterStart:hh\\:mm\\:ss\\.fff}-{round.CenterEnd:hh\\:mm\\:ss\\.fff}|{clipsSig}";
            }

            public void AddCachedResult(TimeSpan originalStart, TimingRound round, CachedCandidateResponse result)
            {
                CachedResults[BuildKey(originalStart, round)] = result;
            }

            public bool TryGetCachedResult(TimeSpan originalStart, TimingRound round, out CachedCandidateResponse result)
            {
                return CachedResults.TryGetValue(BuildKey(originalStart, round), out result);
            }
        }

        private class MultiClipBinaryDataExtractorCollection : BinaryDataExtractorCachedCollection
        {
            private readonly Dictionary<TimeSpan, List<TimingClipOption>> r_clipsByStartTime;

            public MultiClipBinaryDataExtractorCollection(
                BinaryDataExtractorExtended[] extractors,
                Dictionary<TimeSpan, List<TimingClipOption>> clipsByStartTime)
                : base(extractors)
            {
                r_clipsByStartTime = clipsByStartTime ?? new Dictionary<TimeSpan, List<TimingClipOption>>();
            }

            public override Dictionary<string, AIRequestPart[]> GetNamedContentListForItem(
                AIRequestSection section,
                TimedItemWithMetadata item,
                string text = null,
                bool silent = false)
            {
                var audioExtractor = r_extractors.Values.FirstOrDefault(e => e.Extractor is BinaryDataExtractorAudio);
                if (audioExtractor != null && r_clipsByStartTime.TryGetValue(item.StartTime, out var clips) && clips != null && clips.Count > 0)
                {
                    var result = new Dictionary<string, AIRequestPart[]>();
                    foreach (var clip in clips)
                    {
                        var timing = new Timing(clip.Start, clip.End);
                        var clipData = GetNamedContentListForTiming(section, timing, text, silent);
                        if (clipData.TryGetValue(audioExtractor.Extractor.OutputFieldName, out var parts))
                        {
                            result[clip.FieldName] = parts;
                        }
                    }

                    // Also include any non-audio extractors if configured
                    foreach (var kvp in r_extractors.Where(e => !(e.Value.Extractor is BinaryDataExtractorAudio)))
                    {
                        if (kvp.Value.Extractor.MetadataForSkipping == null || !item.Metadata.ContainsKey(kvp.Value.Extractor.MetadataForSkipping))
                        {
                            result[kvp.Key] = kvp.Value.GetData(section, item, text, silent);
                        }
                    }

                    return result;
                }

                return base.GetNamedContentListForItem(section, item, text, silent);
            }
        }

        private Dictionary<TimeSpan, List<TimingClipOption>> m_activeClipsByStartTime;

        protected override BinaryDataExtractorCachedCollection CreateBinaryDataExtractorCollection(
            BinaryDataExtractorExtended[] extractors)
        {
            if (m_activeClipsByStartTime != null)
            {
                return new MultiClipBinaryDataExtractorCollection(extractors, m_activeClipsByStartTime);
            }
            return base.CreateBinaryDataExtractorCollection(extractors);
        }

        [JsonProperty(Order = 20, Required = Required.Always)]
        public string SourceAudioIdForRefiner { get; set; }

        [JsonProperty(Order = 21)]
        public int MaxRetriesPerNode { get; set; } = 3;

        [JsonProperty(Order = 22)]
        public int MaxMountainSteps { get; set; } = 2;

        [JsonProperty(Order = 23)]
        public TimeSpan MaxExpansion { get; set; } = TimeSpan.FromSeconds(2.0);

        [JsonProperty(Order = 24)]
        public TimeSpan BleedDetectionMargin { get; set; } = TimeSpan.FromSeconds(3.0);

        [JsonProperty(Order = 25)]
        public TimeSpan OutwardNudgeDuration { get; set; } = TimeSpan.FromMilliseconds(30);

        [JsonProperty(Order = 26)]
        public TimeSpan MinSubtitleDuration { get; set; } = TimeSpan.FromSeconds(0.5);

        [JsonProperty(Order = 27)]
        public double MaxBudgetRatio { get; set; } = 2.0;

        [JsonProperty(Order = 28)]
        public float ValleyReach { get; set; } = 0.25f;

        [JsonProperty(Order = 29)]
        public bool IncludeTraceInOutputAlways { get; set; } = false;

        [JsonProperty(Order = 30)]
        public bool IncludeTraceInOutputOnError { get; set; } = true;

        [JsonProperty(Order = 31)]
        public bool IncludeTextInOutput { get; set; } = true;

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
                maxMountainSteps: this.MaxMountainSteps,
                maxExpansion: this.MaxExpansion,
                bleedDetectionMargin: this.BleedDetectionMargin,
                outwardNudgeDuration: this.OutwardNudgeDuration,
                minSubtitleDuration: this.MinSubtitleDuration,
                valleyReach: this.ValleyReach);

            context.WriteInfo($"  Preparation: Terrain mapping has {refiner.TerrainMapping.Segments.Count(f => !f.IsSilence)} possible speech segments, {refiner.TerrainMapping.Segments.Count(f => f.IsSilence)} silences.");
            DoExportSrt(context, transcription.Id, refiner.TerrainMapping.ToSegmentSubtitles(), "TERRAIN-MAP");

            var aggregation = this.Metadatas?.Aggregate(context);
            var originalItems = (aggregation?.ReferenceTimingsWithMetadata ?? Array.Empty<TimedItemWithMetadata>())
                .OrderBy(f => f.StartTime)
                .ToList();

            context.WriteInfo($"  Preparation: Initializing acoustic bounds on {originalItems.Count} candidates...");
            foreach (var item in originalItems)
            {
                item.Metadata.TryGetValue(this.Options.MetadataNeeded, out var originalText);
                refiner.AddCandidate(item, originalText ?? "");
            }

            int iteration = 0;
            int totalTestsRun = 0;
            int totalAllowedTests = (int)(originalItems.Count * this.MaxBudgetRatio);

            while (totalTestsRun < totalAllowedTests && iteration <= this.MaxRetriesPerNode)
            {
                refiner.PrepareNextRound();

                var candidatesToTest = refiner.Candidates
                    .Where(c => !c.IsFullyLocked && c.CurrentRound != null && c.RetryCount < this.MaxRetriesPerNode)
                    .ToList();

                iteration++;
                int startLocked = refiner.Candidates.Count(c => c.StartLocked);
                int endLocked = refiner.Candidates.Count(c => c.EndLocked);

                context.WriteInfo($"  Iteration {iteration}: {startLocked}/{refiner.Candidates.Count} start locked, {endLocked}/{refiner.Candidates.Count} end locked, testing {candidatesToTest.Count} candidates, total allowed tests left: {totalAllowedTests - totalTestsRun}...");

                if (candidatesToTest.Count == 0)
                    break;

                string metadataProduced = string.IsNullOrWhiteSpace(this.InnerOptions.MetadataAlwaysProduced)
                    ? "BestStart,BestEnd"
                    : this.InnerOptions.MetadataAlwaysProduced;

                var currentBatchTranscription = new Transcription(
                    id: $"{transcription.Id}_refine_iter_{iteration}",
                    metadataAlwaysProduced: metadataProduced,
                    privateMetadataNames: null,
                    language: transcription.Language,
                    isFinished: false);

                var itemsToBeProcessed = new List<TimedItemWithMetadata>();
                m_activeClipsByStartTime = new Dictionary<TimeSpan, List<TimingClipOption>>();

                foreach (var candidate in candidatesToTest)
                {
                    if (candidate.CurrentRound != null && totalTestsRun < totalAllowedTests)
                    {
                        var round = candidate.CurrentRound;
                        if (jobState.TryGetCachedResult(candidate.OriginalStartTime, round, out var cached))
                        {
                            var cachedMeta = new MetadataCollection();
                            if (cached.BestStart != null) cachedMeta["BestStart"] = cached.BestStart;
                            if (cached.BestEnd != null) cachedMeta["BestEnd"] = cached.BestEnd;
                            if (cached.HeardText != null) cachedMeta["HeardText"] = cached.HeardText;
                            currentBatchTranscription.AddNewItem(candidate.OriginalStartTime, candidate.OriginalEndTime, cachedMeta);
                        }
                        else
                        {
                            var nodeMeta = new MetadataCollection();
                            nodeMeta["OriginalText"] = candidate.OriginalText ?? "";
                            if (!string.IsNullOrWhiteSpace(candidate.PossibleBleedFromPrev))
                            {
                                nodeMeta["PossibleBleedFromPrev"] = candidate.PossibleBleedFromPrev;
                            }
                            if (!string.IsNullOrWhiteSpace(candidate.PossibleBleedFromNext))
                            {
                                nodeMeta["PossibleBleedFromNext"] = candidate.PossibleBleedFromNext;
                            }

                            itemsToBeProcessed.Add(new TimedItemWithMetadata(
                                candidate.OriginalStartTime,
                                candidate.OriginalEndTime,
                                nodeMeta));

                            m_activeClipsByStartTime[candidate.OriginalStartTime] = round.Clips;
                        }
                        totalTestsRun++;
                    }
                }

                try
                {
                    if (itemsToBeProcessed.Count > 0)
                    {
                        var requestGenerator = ExecuteRequests(context, currentBatchTranscription, this.InnerOptions, itemsToBeProcessed.ToArray());
                        if (!requestGenerator.IsFinished())
                            return; // Interrupted
                    }
                }
                finally
                {
                    m_activeClipsByStartTime = null;
                    transcription.Costs.AddRange(currentBatchTranscription.Costs);

                    foreach (var item in currentBatchTranscription.Items)
                    {
                        var cand = candidatesToTest.FirstOrDefault(c => c.OriginalStartTime == item.StartTime);
                        if (cand?.CurrentRound != null)
                        {
                            item.Metadata.TryGetValue("BestStart", out var bs);
                            item.Metadata.TryGetValue("BestEnd", out var be);
                            item.Metadata.TryGetValue("HeardText", out var ht);
                            jobState.AddCachedResult(
                                cand.OriginalStartTime,
                                cand.CurrentRound,
                                new CachedCandidateResponse
                                {
                                    BestStart = bs,
                                    BestEnd = be,
                                    HeardText = ht
                                });
                        }
                    }
                    context.WIP.Save();
                }

                refiner.ProcessBatchResults(currentBatchTranscription.Items);
                UpdateTranscription(transcription, refiner.Candidates);
                DoExportMetatadaSrt(context, transcription, false);
            }

            refiner.FinalizeOverlapsAndMinimumDuration();
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
            int reviewCount = 0;

            int missingStartCount = 0;
            int missingEndCount = 0;
            int mismatchCount = 0;
            int inaudibleCount = 0;
            int overlapCount = 0;
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
                    if (cand.ValidationNotes.Any(n => n.IndexOf("Overlaps", StringComparison.OrdinalIgnoreCase) >= 0))
                        overlapCount++;
                }
                else
                {
                    cleanCount++;
                }
            }

            int resolvedInOneRound = candidates.Count(c => c.Rounds.Count <= 1 && c.IsFullyLocked);
            int refinedWithRetries = total - resolvedInOneRound;

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
            context.WriteInfo($"  Total Candidates: {total} | Total Node Requests Run: {totalTestsRun} (Budget: {totalAllowedTests}, {((double)totalTestsRun / Math.Max(1, total)):0.00} reqs/node)");
            context.WriteInfo("");
            context.WriteInfo("  QUALITY BREAKDOWN:");
            context.WriteInfo($"    [OK]   Clean / Full Coverage:       {cleanCount,4}  ({(cleanCount * 100.0 / total):0.0}%)  Both sides locked, no warnings");
            context.WriteInfo($"    [WARN] Review Needed:               {reviewCount,4}  ({(reviewCount * 100.0 / total):0.0}%)");
            if (missingStartCount > 0)
                context.WriteInfo($"           - Missing Start (Max limit):   {missingStartCount,4}");
            if (missingEndCount > 0)
                context.WriteInfo($"           - Missing End (Max limit):     {missingEndCount,4}");
            if (mismatchCount > 0)
                context.WriteInfo($"           - Text Mismatch (in Max):      {mismatchCount,4}");
            if (inaudibleCount > 0)
                context.WriteInfo($"           - Audio Inaudible:             {inaudibleCount,4}");
            if (overlapCount > 0)
                context.WriteInfo($"           - Overlap Warning:             {overlapCount,4}");
            if (unfinishedCount > 0)
                context.WriteInfo($"           - Unfinished:                  {unfinishedCount,4}");
            context.WriteInfo("");
            context.WriteInfo("  TIMING ADJUSTMENTS & ACCURACY:");
            context.WriteInfo($"    - Resolved in Round 1:              {resolvedInOneRound,4}  ({(resolvedInOneRound * 100.0 / total):0.0}%)");
            context.WriteInfo($"    - Needed Round 2+:                  {refinedWithRetries,4}  ({(refinedWithRetries * 100.0 / total):0.0}%)");
            context.WriteInfo($"    - Avg Duration:                     {avgOrigDur:0.00}s -> {avgFinalDur:0.00}s (diff: {(avgFinalDur - avgOrigDur):+0.00;-0.00;0.00}s)");
            context.WriteInfo($"    - Avg Start Drift:                  {avgStartDrift:+0.00;-0.00;0.00}s (max outward: {maxOutwardStartDrift:0.00}s)");
            context.WriteInfo($"    - Avg End Drift:                    {avgEndDrift:+0.00;-0.00;0.00}s (max outward: {maxOutwardEndDrift:+0.00;-0.00;0.00}s)");
            context.WriteInfo($"    - Overlaps / Squashed (<=0.05s):    {overlaps} / {squashed}");
            context.WriteInfo("================================================================================");
        }
    }
}
