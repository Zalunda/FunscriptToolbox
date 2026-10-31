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
    /// (Audio_Max_Max, then Audio_C1_C1 .. Audio_CK_CK) per subtitle and asking the AI to select the best Start and End boundaries.
    /// </summary>
    public class TranscriberAIRefineTiming : TranscriberAI, ITimingRefinerSettings
    {
        // ---------------------------------------------------------------------
        // Cache
        // ---------------------------------------------------------------------

        public class CachedCandidateResponse
        {
            public string BestStart { get; set; }
            public string BestEnd { get; set; }
            public string HeardText { get; set; }

            public bool IsEmpty => BestStart == null && BestEnd == null && HeardText == null;

            public static CachedCandidateResponse FromMetadata(MetadataCollection metadata)
            {
                metadata.TryGetValue(TimingMeta.BestStart, out var bestStart);
                metadata.TryGetValue(TimingMeta.BestEnd, out var bestEnd);
                metadata.TryGetValue(TimingMeta.HeardText, out var heardText);
                return new CachedCandidateResponse
                {
                    BestStart = bestStart,
                    BestEnd = bestEnd,
                    HeardText = heardText
                };
            }

            public MetadataCollection ToMetadata()
            {
                var metadata = new MetadataCollection();
                if (BestStart != null) metadata[TimingMeta.BestStart] = BestStart;
                if (BestEnd != null) metadata[TimingMeta.BestEnd] = BestEnd;
                if (HeardText != null) metadata[TimingMeta.HeardText] = HeardText;
                return metadata;
            }
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

        // ---------------------------------------------------------------------
        // Multi-clip extractor
        // ---------------------------------------------------------------------

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

        // ---------------------------------------------------------------------
        // Settings (ITimingRefinerSettings members are the ones with Order 21..28)
        // ---------------------------------------------------------------------

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
        public TimeSpan TinyOverlapTolerance { get; set; } = TimeSpan.FromMilliseconds(30);

        [JsonProperty(Order = 26)]
        public double MaxBudgetRatio { get; set; } = 2.0;

        [JsonProperty(Order = 27)]
        public float ValleyReach { get; set; } = 0.25f;

        [JsonProperty(Order = 28)]
        public bool IncludeTraceInOutputAlways { get; set; } = false;

        [JsonProperty(Order = 29)]
        public bool IncludeTraceInOutputOnError { get; set; } = true;

        [JsonProperty(Order = 30)]
        public bool IncludeTextInOutput { get; set; } = true;

        [JsonProperty(Order = 34, Required = Required.Always)]
        public AIOptions InnerOptions { get; set; } = new AIOptions();

        // ---------------------------------------------------------------------
        // Prerequisites
        // ---------------------------------------------------------------------

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

        // ---------------------------------------------------------------------
        // Main loop
        // ---------------------------------------------------------------------

        /// <summary>Everything needed to send one iteration to the AI.</summary>
        private class RefineBatch
        {
            public Transcription Transcription;
            public List<TimedItemWithMetadata> ItemsToProcess = new List<TimedItemWithMetadata>();
            public Dictionary<TimeSpan, List<TimingClipOption>> ClipsByStartTime = new Dictionary<TimeSpan, List<TimingClipOption>>();

            /// <summary>Candidates really sent to the AI (not answered from the cache), by OriginalStartTime.</summary>
            public Dictionary<TimeSpan, TimingCandidate> NewlyRequested = new Dictionary<TimeSpan, TimingCandidate>();
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Transcription transcription)
        {
            var jobState = GetOrCreateJobState(transcription);
            var refiner = CreateRefiner(context, transcription);
            LoadCandidates(context, refiner);

            int iteration = 0;
            int totalTestsRun = 0;
            int totalAllowedTests = (int)(refiner.Candidates.Count * this.MaxBudgetRatio);

            while (totalTestsRun < totalAllowedTests && iteration <= this.MaxRetriesPerNode)
            {
                refiner.PrepareNextRound();

                // PrepareNextRound leaves CurrentRound == null on locked / exhausted candidates
                var candidatesToTest = refiner.Candidates.Where(c => c.CurrentRound != null).ToList();

                iteration++;
                context.WriteInfo($"  Iteration {iteration}: " +
                    $"{refiner.Candidates.Count(c => c.Start.Locked)}/{refiner.Candidates.Count} start locked, " +
                    $"{refiner.Candidates.Count(c => c.End.Locked)}/{refiner.Candidates.Count} end locked, " +
                    $"testing {candidatesToTest.Count} candidates, total allowed tests left: {totalAllowedTests - totalTestsRun}...");

                if (candidatesToTest.Count == 0)
                    break;

                var batch = BuildBatch(transcription, iteration, candidatesToTest, jobState, ref totalTestsRun, totalAllowedTests);
                if (!ExecuteBatch(context, transcription, jobState, batch))
                    return; // Interrupted

                refiner.ProcessBatchResults(batch.Transcription.Items);
                UpdateTranscription(transcription, refiner.Candidates);
                DoExportMetatadaSrt(context, transcription, false);
            }

            refiner.FinalizeOverlapsAndMinimumDuration();
            UpdateTranscription(transcription, refiner.Candidates);
            PrintSummary(context, transcription, refiner, totalTestsRun, totalAllowedTests);
            transcription.MarkAsFinished();
            context.WIP.Save();
        }

        private static JobState GetOrCreateJobState(Transcription transcription)
        {
            var jobState = transcription.CurrentJobState as JobState;
            if (jobState == null)
            {
                jobState = new JobState();
                transcription.CurrentJobState = jobState;
            }
            return jobState;
        }

        private TimingRefiner CreateRefiner(SubtitleGeneratorContext context, Transcription transcription)
        {
            var fullPcmAudioForRefiner = context.WIP.AudioExtractions
                .First(f => f.Id == this.SourceAudioIdForRefiner && f.IsFinished)
                .PcmAudio;

            var refiner = new TimingRefiner(fullPcmAudioForRefiner, this);

            var segments = refiner.TerrainMapping.Segments;
            context.WriteInfo($"  Preparation: Terrain mapping has {segments.Count(f => !f.IsSilence)} possible speech segments, {segments.Count(f => f.IsSilence)} silences.");
            DoExportSrt(context, transcription.Id, refiner.TerrainMapping.ToSegmentSubtitles(), "TERRAIN-MAP");

            return refiner;
        }

        private void LoadCandidates(SubtitleGeneratorContext context, TimingRefiner refiner)
        {
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
        }

        // ---------------------------------------------------------------------
        // One iteration: build -> execute -> cache
        // ---------------------------------------------------------------------

        private RefineBatch BuildBatch(
            Transcription transcription,
            int iteration,
            List<TimingCandidate> candidatesToTest,
            JobState jobState,
            ref int totalTestsRun,
            int totalAllowedTests)
        {
            string metadataProduced = string.IsNullOrWhiteSpace(this.InnerOptions.MetadataAlwaysProduced)
                ? $"{TimingMeta.BestStart},{TimingMeta.BestEnd}"
                : this.InnerOptions.MetadataAlwaysProduced;

            var batch = new RefineBatch
            {
                Transcription = new Transcription(
                    id: $"{transcription.Id}_refine_iter_{iteration}",
                    metadataAlwaysProduced: metadataProduced,
                    privateMetadataNames: null,
                    language: transcription.Language,
                    isFinished: false)
            };

            foreach (var candidate in candidatesToTest)
            {
                if (totalTestsRun >= totalAllowedTests)
                    break;

                var round = candidate.CurrentRound;
                if (jobState.TryGetCachedResult(candidate.OriginalStartTime, round, out var cached))
                {
                    batch.Transcription.AddNewItem(candidate.OriginalStartTime, candidate.OriginalEndTime, cached.ToMetadata());
                }
                else
                {
                    batch.ItemsToProcess.Add(new TimedItemWithMetadata(
                        candidate.OriginalStartTime,
                        candidate.OriginalEndTime,
                        BuildNodeMetadata(candidate)));
                    batch.ClipsByStartTime[candidate.OriginalStartTime] = round.Clips;
                    batch.NewlyRequested[candidate.OriginalStartTime] = candidate;
                }
                totalTestsRun++;
            }

            return batch;
        }

        private static MetadataCollection BuildNodeMetadata(TimingCandidate candidate)
        {
            var nodeMeta = new MetadataCollection();
            nodeMeta[TimingMeta.OriginalText] = candidate.OriginalText ?? "";
            if (!string.IsNullOrWhiteSpace(candidate.PossibleBleedFromPrev))
                nodeMeta[TimingMeta.PossibleBleedFromPrev] = candidate.PossibleBleedFromPrev;
            if (!string.IsNullOrWhiteSpace(candidate.PossibleBleedFromNext))
                nodeMeta[TimingMeta.PossibleBleedFromNext] = candidate.PossibleBleedFromNext;
            return nodeMeta;
        }

        /// <returns>false when the requests were interrupted (the caller must stop).</returns>
        private bool ExecuteBatch(
            SubtitleGeneratorContext context,
            Transcription transcription,
            JobState jobState,
            RefineBatch batch)
        {
            m_activeClipsByStartTime = batch.ClipsByStartTime;
            try
            {
                if (batch.ItemsToProcess.Count > 0)
                {
                    var requestGenerator = ExecuteRequests(context, batch.Transcription, this.InnerOptions, batch.ItemsToProcess.ToArray());
                    if (!requestGenerator.IsFinished())
                        return false;
                }
                return true;
            }
            finally
            {
                m_activeClipsByStartTime = null;
                transcription.Costs.AddRange(batch.Transcription.Costs);
                CacheNewResults(jobState, batch);
                context.WIP.Save();
            }
        }

        /// <summary>Caches only answers that really came from the AI (never re-caches cache hits, never caches empty answers).</summary>
        private static void CacheNewResults(JobState jobState, RefineBatch batch)
        {
            foreach (var item in batch.Transcription.Items)
            {
                if (!batch.NewlyRequested.TryGetValue(item.StartTime, out var candidate) || candidate.CurrentRound == null)
                    continue;

                var response = CachedCandidateResponse.FromMetadata(item.Metadata);
                if (!response.IsEmpty)
                    jobState.AddCachedResult(candidate.OriginalStartTime, candidate.CurrentRound, response);
            }
        }

        // ---------------------------------------------------------------------
        // Output
        // ---------------------------------------------------------------------

        private void UpdateTranscription(
            Transcription transcription,
            List<TimingCandidate> candidates)
        {
            var newItems = new List<TranscribedItem>();
            foreach (var candidate in candidates)
            {
                var meta = new MetadataCollection();
                foreach (var kvp in candidate.OriginalItem.Metadata)
                    meta[kvp.Key] = kvp.Value;

                meta[this.Options.MetadataAlwaysProduced] = BuildOutputText(candidate);
                meta[TimingMeta.TimingTrace] = string.Join("\n", candidate.Traces);

                newItems.Add(new TranscribedItem(
                    candidate.Start.Current,
                    candidate.End.Current,
                    meta,
                    (candidate.OriginalItem as TranscribedItem)?.NoSpeechProbability ?? 0.0,
                    (candidate.OriginalItem as TranscribedItem)?.Words));
            }

            transcription.Items.Clear();
            transcription.Items.AddRange(newItems);
        }

        private string BuildOutputText(TimingCandidate candidate)
        {
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
                    sb.AppendLine($"  {trace}");
            }

            if (this.IncludeTextInOutput)
            {
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.AppendLine(candidate.OriginalText);
            }

            return sb.ToString();
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
            var s = refiner.ComputeStats();
            if (s == null)
                return;

            const string line = "================================================================================";
            context.WriteInfo(line);
            context.WriteInfo($"  TIMING REFINER SUMMARY ({transcription.Id})");
            context.WriteInfo(line);
            context.WriteInfo($"  Total Candidates: {s.Total} | Total Node Requests Run: {totalTestsRun} (Budget: {totalAllowedTests}, {((double)totalTestsRun / Math.Max(1, s.Total)):0.00} reqs/node)");
            context.WriteInfo("");
            context.WriteInfo("  QUALITY BREAKDOWN:");
            context.WriteInfo($"    [OK]   Clean / Full Coverage:       {s.Clean,4}  ({Percent(s.Clean, s.Total):0.0}%)  Both sides locked, no warnings");
            context.WriteInfo($"    [WARN] Review Needed:               {s.Review,4}  ({Percent(s.Review, s.Total):0.0}%)");

            foreach (var (key, label) in TimingRefinerStats.NoteCategories)
                PrintReviewLine(context, label, s.NoteCounts[key]);
            PrintReviewLine(context, "Unfinished", s.Unfinished);

            context.WriteInfo("");
            context.WriteInfo("  TIMING ADJUSTMENTS & ACCURACY:");
            context.WriteInfo($"    - Resolved in Round 1:              {s.ResolvedInOneRound,4}  ({Percent(s.ResolvedInOneRound, s.Total):0.0}%)");
            context.WriteInfo($"    - Needed Round 2+:                  {s.RefinedWithRetries,4}  ({Percent(s.RefinedWithRetries, s.Total):0.0}%)");
            context.WriteInfo($"    - Avg Duration:                     {s.AvgOriginalDuration:0.00}s -> {s.AvgFinalDuration:0.00}s (diff: {(s.AvgFinalDuration - s.AvgOriginalDuration):+0.00;-0.00;0.00}s)");
            context.WriteInfo($"    - Avg Start Drift:                  {s.AvgStartDrift:+0.00;-0.00;0.00}s (max outward: {s.MaxOutwardStartDrift:0.00}s)");
            context.WriteInfo($"    - Avg End Drift:                    {s.AvgEndDrift:+0.00;-0.00;0.00}s (max outward: {s.MaxOutwardEndDrift:+0.00;-0.00;0.00}s)");
            context.WriteInfo($"    - Overlaps / Squashed (<=0.05s):    {s.Overlaps} / {s.Squashed}");
            context.WriteInfo(line);
        }

        private static double Percent(int count, int total) => count * 100.0 / Math.Max(1, total);

        private static void PrintReviewLine(SubtitleGeneratorContext context, string label, int count)
        {
            if (count > 0)
                context.WriteInfo($"           - {(label + ":"),-29}{count,4}");
        }
    }
}
