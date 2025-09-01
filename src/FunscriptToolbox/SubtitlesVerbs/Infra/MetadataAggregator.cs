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

        [JsonProperty(Order = 1)]
        public string TimingsSource { get; set; }
        [JsonProperty(Order = 2)]
        public string[] Sources { get; set; }
        [JsonProperty(Order = 3)]
        public Dictionary<string, string> MergeRules { get; set; }

        public MetadataAggregation Aggregate(
            SubtitleGeneratorContext context,
            TimedItemWithMetadataCollection additionalSource = null)
        {
            var timings = context.WIP?.Transcriptions?.FirstOrDefault(t => t.Id == this.TimingsSource && t.IsFinished)?.GetTimings();
            var (rawSourceReferences, reasonsFromSourcesReferences) = GetRawSources(context, this.Sources, additionalSource);
            var referenceTimingsWithMetadata = timings != null ? MergeRawSources(timings, rawSourceReferences, this.MergeRules) : null;
            return new MetadataAggregation(
                this.TimingsSource,
                reasonsFromSourcesReferences,
                referenceTimingsWithMetadata);
        }

        public (TimedItemWithMetadataCollection[], string[]) GetOthersSources(
            SubtitleGeneratorContext context, 
            string[] sources)
        {
            return GetRawSources(context, sources);
        }

        private static (TimedItemWithMetadataCollection[], string[]) GetRawSources(
            SubtitleGeneratorContext context,
            string[] sources,
            TimedItemWithMetadataCollection additionalSource = null)
        {
            var metadataProviders = new List<TimedItemWithMetadataCollection>();
            var reasons = new List<string>();
            if (additionalSource != null)
                metadataProviders.Add(additionalSource);

            foreach (var rule in sources ?? Array.Empty<string>())
            {
                var pattern = rule.Contains("*") 
                    ? rule.Replace("*", ".*").Replace("..*", ".*") 
                    : rule;
                var regex = new Regex($"^{pattern}$");

                foreach (var transcriber in context.Config.Workers.OfType<Transcriber>()
                        .Where(t => regex.IsMatch(t.TranscriptionId)))
                {
                    var transcription = context.WIP.Transcriptions.FirstOrDefault(t => t.Id == transcriber.TranscriptionId);

                    if (transcriber.Enabled && transcription?.IsFinished != true)
                    {
                        reasons.Add($"Transcription '{transcriber.TranscriptionId}' is not done yet.");
                    }
                    if (transcription?.IsFinished == true)
                    {
                        metadataProviders.Add(transcription);
                    }
                }

                foreach (var translator in context.Config.Workers.OfType<Translator>()
                        .Where(t => regex.IsMatch(t.FullId)))
                {
                    var translation = context.WIP.Translations
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

            return (
                metadataProviders.Distinct().ToArray(),
                reasons.Distinct().ToArray());
        }

        private static TimedItemWithMetadata[] MergeRawSources(
            ITiming[] timings,
            TimedItemWithMetadataCollection[] rawReferenceSources,
            Dictionary<string, string> mergeRules)
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
                        // TODO Low priority: Allow to have a key "sourcename:key" which would apply the merge rule only on item from sourcename.
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