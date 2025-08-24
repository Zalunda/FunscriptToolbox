using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs
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

        public bool IsPrerequisitesMetIncludingTimings(
            SubtitleGeneratorContext context,
            out string reason)
        {
            return IsPrerequisitesMet(context, out reason, true);
        }

        public bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason,
            bool timingsRequired = false)
        {
            var (reasons, _, _) = ValidateMetadataProviders(context, timingsRequired);
            if (reasons.Any())
            {
                reason = string.Join("\n", reasons);
                return false;
            }

            reason = null;
            return true;
        }

        private (string[], ITiming[], List<ITimedObjectWithMetadataCollection>) ValidateMetadataProviders(
            SubtitleGeneratorContext context, 
            bool timingsRequired)
        {
            var reasons = new List<string>();
            var metadataProviders = new List<ITimedObjectWithMetadataCollection>();

            var timings = context.CurrentWipsub?.Transcriptions?.FirstOrDefault(t => t.Id == this.TimingsSource && t.IsFinished)?.GetTimings();
            if (timingsRequired && timings == null)
            {
                reasons.Add($"Transcription '{this.TimingsSource}' is not done yet (for timings).");
            }

            foreach (var rule in this.Sources)
            {
                var match = Regex.Match(rule, @"^(?<transcriptionId>[^\/\\]*)([\/\\](?<translationId>.*))?");
                var transcriptionId = match.Groups["transcriptionId"].Value;
                var translationId = match.Groups["translationId"].Value;
                if (!string.IsNullOrWhiteSpace(transcriptionId))
                {
                    foreach (var transcriber in context.Config.Transcribers.Where(t => transcriptionId == "*" || t.TranscriptionId == transcriptionId))
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
                            foreach (var translator in transcriber.Translators.Where(t => translationId == "*" || t.TranslationId == translationId))
                            {
                                var translation = transcription.Translations.FirstOrDefault(t => t.Id == translator.TranslationId);

                                if (translator.Enabled && translation?.IsFinished != true)
                                {
                                    reasons.Add($"Translation '{transcriber.TranscriptionId}/{translator.TranslationId}' is not done yet.");
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
                reasons.Distinct().ToArray(),
                timings,
                metadataProviders.Distinct().ToList());
        }

        internal TimedObjectWithMetadata<T>[] GetTimingsWithMetadata<T>(
            SubtitleGeneratorContext context, 
            ITimedObjectWithMetadataCollection workingOn = null)
        {
            var (_, timings, timedObjectsWithMetadata) = ValidateMetadataProviders(context, true);
            if (workingOn != null)
            {
                timedObjectsWithMetadata.Add(workingOn);
            }

            var allItems = timedObjectsWithMetadata.SelectMany(t => t.Items).ToList();
            var mergedItems = new List<TimedObjectWithMetadata<T>>();
            foreach (var timing in timings)
            {
                var metadata = new MetadataCollection();
                foreach (var timedObjectWithMetadata in timedObjectsWithMetadata)
                {
                    foreach (var item in timedObjectWithMetadata.Items.Where(item => item.StartTime < timing.EndTime && item.EndTime > timing.StartTime))
                    {
                        allItems.Remove(item);
                        metadata.Merge(item.Metadata);
                    }
                }
                mergedItems.Add(new TimedObjectWithMetadata<T>(timing.StartTime, timing.EndTime, metadata));
            }
            if (allItems.Count > 0)
            {
                throw new Exception("TODO What to do with left over.");
            }
            return mergedItems
                .OrderBy(f => f.StartTime)
                .ToArray();
        }
    }
}