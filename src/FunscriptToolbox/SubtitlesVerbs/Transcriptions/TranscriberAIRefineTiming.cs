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

        [JsonProperty(Order = 34, Required = Required.Always)]
        public AIOptions InnerOptions { get; set; } = new AIOptions();

        // Maximum number of rounds per candidate (a round tests 1 window).
        [JsonProperty(Order = 43)]
        public int MaxRetriesPerNode { get; set; } = 8;

        [JsonProperty(Order = 44)]
        public double MaxBudgetRatio { get; set; } = 8.0;

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
            var refiner = new TimingRefiner(fullPcmAudioForRefiner)
            {
                MaxRetriesPerNode = this.MaxRetriesPerNode
            };
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
            while (totalTestsRun < totalAllowedTests)
            {
                var candidatesToTest = refiner.Candidates
                    .Where(c => !c.IsFullyLocked && c.RetryCount < MaxRetriesPerNode)
                    .ToList();
                if (candidatesToTest.Count == 0)
                    break;

                iteration++;
                int lockedNodes = refiner.Candidates.Count(c => c.IsFullyLocked);
                context.WriteInfo($"  Iteration {iteration}: {lockedNodes}/{refiner.Candidates.Count} fully locked, testing {candidatesToTest.Count} candidates, total allowed tests left: {totalAllowedTests - totalTestsRun}...");

                var currentBatchTranscription = new Transcription(
                    id: $"{transcription.Id}_refine_iter_{iteration}",
                    metadataAlwaysProduced: this.InnerOptions.MetadataAlwaysProduced,
                    privateMetadataNames: null,
                    language: transcription.Language,
                    isFinished: false);

                var itemsToBeProcessed = new List<TimedItemWithMetadata>();
                foreach (var candidate in candidatesToTest)
                {
                    if (candidate.Plan != null)
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

                totalTestsRun += currentBatchTranscription.Items.Count;
            }

            return; // Don't want to finalize or I can't retry easily
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
                    sb.AppendLine("Timings Warnings:");
                    if (!candidate.IsFullyLocked)
                        sb.AppendLine("Unsure: Unfinished");
                    foreach (var note in candidate.ValidationNotes)
                        sb.AppendLine("  " + note);
                }

                if (sb.Length > 0)
                    sb.AppendLine();
                sb.AppendLine(candidate.OriginalText);

                meta[this.Options.MetadataAlwaysProduced] = sb.ToString();
                meta["TimingTrace"] = string.Join("\n", candidate.Trace);

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
    }
}
