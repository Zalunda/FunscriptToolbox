using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using FunscriptToolbox.SubtitlesVerbs.Translations;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class MetadataAggregator
    {
        public MetadataAggregator()
        {
        }

        [JsonProperty(Order = 10)]
        public string TimingsSource { get; set; }
        [JsonProperty(Order = 11)]
        public string[] Sources { get; set; }

        public MetadataAggregation Aggregate(
            SubtitleGeneratorContext context,
            TimedItemWithMetadataCollection additionnalSource = null,
            Dictionary<string, string> mergeRules = null)
        {
            var timings = context.CurrentWipsub?.Transcriptions?.FirstOrDefault(t => t.Id == this.TimingsSource && t.IsFinished)?.GetTimings();
            var (rawSourceReferences, reasonsFromSourcesReferences) = ImportRawSources(context, this.Sources, additionnalSource);
            var referenceTimingsWithMetadata = timings != null ? MergeRawSources(timings, rawSourceReferences, mergeRules) : null;
            return new MetadataAggregation(
                this.TimingsSource,
                rawSourceReferences,
                reasonsFromSourcesReferences,
                referenceTimingsWithMetadata);
        }

        private static (TimedItemWithMetadataCollection[], string[]) ImportRawSources(
            SubtitleGeneratorContext context,
            string[] sources,
            TimedItemWithMetadataCollection additionnalSource)
        {
            var metadataProviders = new List<TimedItemWithMetadataCollection>();
            var reasons = new List<string>();
            if (additionnalSource != null)
                metadataProviders.Add(additionnalSource);

            foreach (var rule in sources ?? Array.Empty<string>())
            {
                var match = Regex.Match(rule, @"^(?<transcriptionId>[^\/\\]*)([\/\\](?<translationId>.*))?");
                var transcriptionId = match.Groups["transcriptionId"].Value;
                var translationId = match.Groups["translationId"].Value;
                if (!string.IsNullOrWhiteSpace(transcriptionId))
                {
                    foreach (var transcriber in context.Config.Workers.OfType<Transcriber>()
                        .Where(t => transcriptionId == "*" || t.TranscriptionId == transcriptionId))
                    {
                        var transcription = context.CurrentWipsub.Transcriptions.FirstOrDefault(t => t.Id == transcriber.TranscriptionId);

                        if (transcriber.Enabled && transcription?.IsFinished != true)
                        {
                            reasons.Add($"Transcription '{transcriber.TranscriptionId}' is not done yet.");
                        }
                        if (transcription?.IsFinished == true)
                        {
                            metadataProviders.Add(transcription);
                        }

                        if (!string.IsNullOrWhiteSpace(translationId))
                        {
                            foreach (var translator in context.Config.Workers.OfType<Translator>()
                                .Where(t => t.TranscriptionId == transcriber.TranscriptionId 
                                    && (translationId == "*" || t.TranslationId == translationId)))
                            {
                                var translation = context.CurrentWipsub.Translations
                                    .FirstOrDefault(t => t.TranscriptionId == translator.TranscriptionId && t.TranslationId == translator.TranslationId);

                                if (translator.Enabled && translation?.IsFinished != true)
                                {
                                    reasons.Add($"Translation '{translator.FullId}' is not done yet.");
                                }
                                if (translation?.IsFinished == true)
                                {
                                    metadataProviders.Add(translation);
                                }
                            }
                        }
                    }
                }
            }

            return (
                metadataProviders.Distinct().ToArray(),
                reasons.Distinct().ToArray());
        }

        private static TimedItemWithMetadata[] MergeRawSources(
            ITiming[] timings,
            TimedItemWithMetadataCollection[] rawReferenceSources,
            Dictionary<string, string> mergeRules
            )
        {
            if (timings == null)
                return null;

            var allItems = rawReferenceSources.SelectMany(t => t.GetItems()).ToList();
            var mergedItems = new List<TimedItemWithMetadata>();
            foreach (var timing in timings)
            {
                var metadata = new MetadataCollection();
                foreach (var timedObjectWithMetadata in rawReferenceSources)
                {
                    var nbMerges = 0;
                    foreach (var item in timedObjectWithMetadata.GetItems().Where(item => item.StartTime < timing.EndTime && item.EndTime > timing.StartTime))
                    {
                        allItems.Remove(item);
                        metadata.Merge(item.Metadata, mergeRules);
                        nbMerges++;
                    }
                    if (nbMerges >= 2)
                    {
                        // TODO 
                        throw new Exception("do we handle this differently? do we merge the values?");
                    }
                }
                mergedItems.Add(new TimedItemWithMetadata(timing.StartTime, timing.EndTime, metadata));
            }
            if (allItems.Count > 0)
            {
                // TODO Handle extra nodes
                throw new Exception("TODO What to do with left over.");
            }
            return mergedItems
                .OrderBy(f => f.StartTime)
                .ToArray();
        }
    }
}