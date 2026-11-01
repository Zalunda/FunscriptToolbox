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
    /// Refines the timing of an existing transcription in two sequential passes:
    ///   1. START pass: Audio_Max, Audio_S1..Audio_Sn (same end, different starts) -> BestStart
    ///   2. END pass:   Audio_Max, Audio_E1..Audio_En (start locked, different ends) -> BestEnd
    /// Each pass has its own prompt/options (InnerOptionsStart / InnerOptionsEnd).
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
                var clipsSig = string.Join(";", round.Clips.Select(c => $"{c.OptionId}:{c.Start:hh\\:mm\\:ss\\.fff}-{c.End:hh\\:mm\\:ss\\.fff}"));
                return $"{originalStart:hh\\:mm\\:ss\\.fff}|{round.Side}|{round.Center:hh\\:mm\\:ss\\.fff}|{StableHash(round.TextSent)}|{clipsSig}";
            }

            /// <summary>64-bit FNV-1a (string.GetHashCode is randomized per process on modern .NET, so it can't be used for a persisted cache).</summary>
            private static string StableHash(string text)
            {
                unchecked
                {
                    ulong hash = 14695981039346656037;
                    foreach (var ch in text ?? "")
                    {
                        hash ^= ch;
                        hash *= 1099511628211;
                    }
                    return hash.ToString("x16");
                }
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
        // Settings (ITimingRefinerSettings members are the ones with Order 21..27, 29)
        // ---------------------------------------------------------------------

        [JsonProperty(Order = 20, Required = Required.Always)]
        public string SourceAudioIdForTerrain { get; set; }

        /// <summary>After the AI's final Start answer, move the Start forward over pure silence (according to the terrain).</summary>
        [JsonProperty(Order = 23)]
        public bool TrimSilenceOnStart { get; set; } = false;

        /// <summary>After the AI's final End answer, move the End back over pure silence (according to the terrain).</summary>
        [JsonProperty(Order = 24)]
        public bool TrimSilenceOnEnd { get; set; } = true;

        [JsonProperty(Order = 25)]
        public TimeSpan MaxExpansion { get; set; } = TimeSpan.FromSeconds(2.0);

        [JsonProperty(Order = 26)]
        public TimeSpan BleedDetectionMargin { get; set; } = TimeSpan.FromSeconds(3.0);

        /// <summary>START pass only: all clips end this long after the initial End (so the AI hears the whole line).</summary>
        [JsonProperty(Order = 27)]
        public TimeSpan StartPassTailPadding { get; set; } = TimeSpan.FromSeconds(0.8);

        [JsonProperty(Order = 28)]
        public float ValleyReach { get; set; } = 0.25f;

        [JsonProperty(Order = 29)]
        public bool IncludeTraceInOutputAlways { get; set; } = false;

        [JsonProperty(Order = 30)]
        public bool IncludeTraceInOutputOnError { get; set; } = true;

        [JsonProperty(Order = 31)]
        public bool IncludeTextInOutput { get; set; } = true;

        /// <summary>Prompt/batching/extractors of the START pass (must produce BestStart, optionally HeardText).</summary>
        [JsonProperty(Order = 40, Required = Required.Always)]
        public AIOptions InnerOptionsStart { get; set; } = new AIOptions();

        /// <summary>Prompt/batching/extractors of the END pass (must produce BestEnd).</summary>
        [JsonProperty(Order = 41, Required = Required.Always)]
        public AIOptions InnerOptionsEnd { get; set; } = new AIOptions();

        private AIOptions InnerOptionsFor(BoundarySide side)
            => side == BoundarySide.Start ? this.InnerOptionsStart : this.InnerOptionsEnd;

        // ---------------------------------------------------------------------
        // Prerequisites
        // ---------------------------------------------------------------------

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (context.WIP.AudioExtractions.Any(f => f.Id == this.SourceAudioIdForTerrain)
                && context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == this.SourceAudioIdForTerrain && f.IsFinished) == null)
            {
                reason = $"Audio extraction '{this.SourceAudioIdForTerrain}' is not done yet.";
                return false;
            }

            foreach (var inner in new[] { this.InnerOptionsStart, this.InnerOptionsEnd })
            {
                foreach (var extractor in inner?.BinaryDataExtractors?.OfType<BinaryDataExtractorAudio>() ?? Array.Empty<BinaryDataExtractorAudio>())
                {
                    if (context.WIP.AudioExtractions.FirstOrDefault(f => f.Id == extractor.SourceAudioId && f.IsFinished) == null)
                    {
                        reason = $"Audio extraction '{extractor.SourceAudioId}' is not done yet.";
                        return false;
                    }
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

        private class PhaseReport
        {
            public BoundarySide Side;
            public int Iterations;
            public int Tests;     // field (not property) so it can be passed by ref
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Transcription transcription)
        {
            var jobState = GetOrCreateJobState(transcription);
            var refiner = CreateRefiner(context, transcription);
            LoadCandidates(context, refiner);

            var reports = new List<PhaseReport>();
            foreach (var side in TimingCandidate.Sides)
            {
                if (!RunPhase(context, transcription, refiner, jobState, side, reports))
                    return; // Interrupted
            }

            refiner.FinalizeOverlaps();
            UpdateTranscription(transcription, refiner.Candidates);
            PrintSummary(context, transcription, refiner, reports);
            transcription.MarkAsFinished();
            context.WIP.Save();
        }

        private bool RunPhase(
            SubtitleGeneratorContext context,
            Transcription transcription,
            TimingRefiner refiner,
            JobState jobState,
            BoundarySide side,
            List<PhaseReport> reports)
        {
            var report = new PhaseReport { Side = side };
            reports.Add(report);

            var options = InnerOptionsFor(side);
            refiner.BeginPhase(side);

            // Ends when PrepareNextRound has nothing left to ask (every boundary locked, or the runaway guard was reached)
            while (true)
            {
                refiner.PrepareNextRound(side);

                // PrepareNextRound leaves CurrentRound == null on locked / exhausted / auto-locked candidates
                var candidatesToTest = refiner.Candidates.Where(c => c.CurrentRound != null).ToList();

                report.Iterations++;
                context.WriteInfo($"  [{side}] Iteration {report.Iterations}: " +
                    $"{refiner.Candidates.Count(c => c[side].Locked)}/{refiner.Candidates.Count} locked, " +
                    $"testing {candidatesToTest.Count} candidates...");

                if (candidatesToTest.Count == 0)
                    break;

                var batch = BuildBatch(transcription, side, report.Iterations, candidatesToTest, jobState, options, ref report.Tests);
                if (!ExecuteBatch(context, transcription, jobState, batch, options))
                    return false; // Interrupted

                refiner.ProcessBatchResults(batch.Transcription.Items, side);
                UpdateTranscription(transcription, refiner.Candidates);
                DoExportMetatadaSrt(context, transcription, false);
            }

            // Whatever is still open on this side is locked and flagged, so the next pass can rely on it
            refiner.EndPhase(side);
            UpdateTranscription(transcription, refiner.Candidates);
            DoExportMetatadaSrt(context, transcription, false);
            context.WriteInfo($"  [{side}] Pass finished: {report.Tests} requests in {report.Iterations - 1} iterations.");
            return true;
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
                .First(f => f.Id == this.SourceAudioIdForTerrain && f.IsFinished)
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
            BoundarySide side,
            int iteration,
            List<TimingCandidate> candidatesToTest,
            JobState jobState,
            AIOptions options,
            ref int totalTestsRun)
        {
            string defaultProduced = side == BoundarySide.Start ? TimingMeta.BestStart : TimingMeta.BestEnd;
            string metadataProduced = string.IsNullOrWhiteSpace(options.MetadataAlwaysProduced)
                ? defaultProduced
                : options.MetadataAlwaysProduced;

            var batch = new RefineBatch
            {
                Transcription = new Transcription(
                    id: $"{transcription.Id}_refine_{side}_iter_{iteration}",
                    metadataAlwaysProduced: metadataProduced,
                    privateMetadataNames: null,
                    language: transcription.Language,
                    isFinished: false)
            };

            foreach (var candidate in candidatesToTest)
            {
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
                        BuildNodeMetadata(candidate, side)));
                    batch.ClipsByStartTime[candidate.OriginalStartTime] = round.Clips;
                    batch.NewlyRequested[candidate.OriginalStartTime] = candidate;
                }
                totalTestsRun++;
            }

            return batch;
        }

        /// <summary>Only what the pass needs: Start gets the previous bleed, End gets the next bleed.</summary>
        private static MetadataCollection BuildNodeMetadata(TimingCandidate candidate, BoundarySide side)
        {
            var nodeMeta = new MetadataCollection();
            nodeMeta[TimingMeta.OriginalText] = candidate.CurrentRound.TextSent ?? "";

            if (side == BoundarySide.Start && !string.IsNullOrWhiteSpace(candidate.PossibleBleedFromPrev))
                nodeMeta[TimingMeta.PossibleBleedFromPrev] = candidate.PossibleBleedFromPrev;

            if (side == BoundarySide.End && !string.IsNullOrWhiteSpace(candidate.PossibleBleedFromNext))
                nodeMeta[TimingMeta.PossibleBleedFromNext] = candidate.PossibleBleedFromNext;

            return nodeMeta;
        }

        /// <returns>false when the requests were interrupted (the caller must stop).</returns>
        private bool ExecuteBatch(
            SubtitleGeneratorContext context,
            Transcription transcription,
            JobState jobState,
            RefineBatch batch,
            AIOptions options)
        {
            m_activeClipsByStartTime = batch.ClipsByStartTime;
            try
            {
                if (batch.ItemsToProcess.Count > 0)
                {
                    var requestGenerator = ExecuteRequests(context, batch.Transcription, options, batch.ItemsToProcess.ToArray());
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
            List<PhaseReport> reports)
        {
            var s = refiner.ComputeStats();
            if (s == null)
                return;

            const string line = "================================================================================";
            context.WriteInfo(line);
            context.WriteInfo($"  TIMING REFINER SUMMARY ({transcription.Id})");
            context.WriteInfo(line);
            context.WriteInfo($"  Total Candidates: {s.Total}");
            foreach (var r in reports)
            {
                context.WriteInfo($"  {r.Side,-5} pass requests: {r.Tests} ({((double)r.Tests / Math.Max(1, s.Total)):0.00} reqs/node)");
            }
            context.WriteInfo("");
            context.WriteInfo("  QUALITY BREAKDOWN:");
            context.WriteInfo($"    [OK]   Clean / Full Coverage:       {s.Clean,4}  ({Percent(s.Clean, s.Total):0.0}%)  Both sides locked, no warnings");
            context.WriteInfo($"    [WARN] Review Needed:               {s.Review,4}  ({Percent(s.Review, s.Total):0.0}%)");

            foreach (var (key, label) in TimingRefinerStats.NoteCategories)
                PrintReviewLine(context, label, s.NoteCounts[key]);

            context.WriteInfo("");
            context.WriteInfo("  TIMING ADJUSTMENTS & ACCURACY:");
            context.WriteInfo($"    - Resolved with <=1 answer/side:    {s.ResolvedInOneRound,4}  ({Percent(s.ResolvedInOneRound, s.Total):0.0}%)");
            context.WriteInfo($"    - Needed 2+ answers on a side:      {s.RefinedWithRetries,4}  ({Percent(s.RefinedWithRetries, s.Total):0.0}%)");
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
                context.WriteInfo($"           - {(label + ":"),-33}{count,4}");
        }
    }
}
