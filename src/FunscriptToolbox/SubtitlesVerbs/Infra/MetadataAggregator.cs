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
        public string Sources { get; set; }
        [JsonProperty(Order = 3)]
        public MergeRuleDictionary MergeRules { get; set; }
        [JsonProperty(Order = 4)]
        public ValueTransformationRuleDictionary ValueTransformationRules { get; set; }

        public MetadataAggregation Aggregate(
            SubtitleGeneratorContext context,
            TimedItemWithMetadataCollection additionalSource = null)
        {
            // (an alias: its source)
            var timingsSource = context.FindSource(this.TimingsSource);
            var timings = timingsSource?.IsFinished == true ? timingsSource.GetItems().Cast<ITiming>().ToArray() : null;
            var (rawSourceReferences, reasonsFromSourcesReferences, aliasesUsed) = GetRawSourcesAndAliases(context, this.Sources?.Split(',').Select(f => f.Trim()).ToArray(), additionalSource);
            var referenceTimingsWithMetadata = timings != null
                ? MergeRawSources(
                    timings,
                    rawSourceReferences,
                    WithAliasRules(this.MergeRules, aliasesUsed),
                    WithAliasRules(this.ValueTransformationRules, aliasesUsed))
                : null;
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

        public TimedItemWithMetadataCollection[] GetSources(
            SubtitleGeneratorContext context,
            TimedItemWithMetadataCollection additionalSource = null)
        {
            var (rawSourceReferences, reasonsFromSourcesReferences) = GetRawSources(
                context, 
                this.Sources?.Split(',').Select(f => f.Trim()).ToArray(), 
                additionalSource);

            return rawSourceReferences;
        }

        /// <summary>
        /// The regex of one source rule (ex. "full-whisper", "singlevad-*"): the id of a transcription / translation must match it.
        /// (also used by the flow graph of the web UI: the same rule as the run)
        /// </summary>
        internal static Regex ToSourceRegex(string rule)
        {
            var pattern = rule.Contains("*")
                ? rule.Replace("*", ".*").Replace("..*", ".*")
                : rule;
            return new Regex($"^{pattern}$");
        }

        private static (TimedItemWithMetadataCollection[], string[]) GetRawSources(
            SubtitleGeneratorContext context,
            string[] sources,
            TimedItemWithMetadataCollection additionalSource = null)
        {
            var (providers, reasons, _) = GetRawSourcesAndAliases(context, sources, additionalSource);
            return (providers, reasons);
        }

        /// <summary>
        /// The sources, and the aliases used to reach them (alias id, id of its source): a rule can match an alias
        /// (see <see cref="TranscriberAlias"/>), the source of the alias is then used (once, even if it's also matched by its own id).
        /// The data saved in the .wipsubtitles under the name of an alias (ex. from when it was a clone) is ignored.
        /// </summary>
        private static (TimedItemWithMetadataCollection[], string[], List<(string alias, string target)>) GetRawSourcesAndAliases(
            SubtitleGeneratorContext context,
            string[] sources,
            TimedItemWithMetadataCollection additionalSource = null)
        {
            var metadataProviders = new List<TimedItemWithMetadataCollection>();
            var reasons = new List<string>();
            var aliasesUsed = new List<(string alias, string target)>();
            if (additionalSource != null)
                metadataProviders.Add(additionalSource);

            var aliases = TranscriberAlias.GetAliases(context.Config);
            var aliasIds = new HashSet<string>(aliases.Select(a => a.TranscriptionId));

            foreach (var rule in sources ?? Array.Empty<string>())
            {
                var regex = ToSourceRegex(rule);

                foreach (var transcriber in context.Config.Workers.OfType<Transcriber>()
                        .Where(t => !(t is TranscriberAlias) && regex.IsMatch(t.TranscriptionId)))
                {
                    var transcription = context.WIP.Transcriptions.FirstOrDefault(t => t.Id == transcriber.TranscriptionId);
                    if (transcriber.Enabled && transcription?.IsFinished != true)
                    {
                        reasons.Add($"Transcription '{transcriber.TranscriptionId}' is not done yet.");
                    }
                }

                metadataProviders.AddRange(context.WIP.Transcriptions.Where(t => regex.IsMatch(t.Id) && !aliasIds.Contains(t.Id)));

                foreach (var translator in context.Config.Workers.OfType<Translator>()
                        .Where(t => regex.IsMatch(t.TranslationId)))
                {
                    var translation = context.WIP.Translations
                        .FirstOrDefault(t => regex.IsMatch(t.Id));
                    if (translator.Enabled && translation?.IsFinished != true)
                    {
                        reasons.Add($"Translation '{translator.TranslationId}' is not done yet.");
                    }
                }

                metadataProviders.AddRange(context.WIP.Translations.Where(t => regex.IsMatch(t.Id) && !aliasIds.Contains(t.Id)));

                // The aliases: their source
                foreach (var alias in aliases.Where(a => regex.IsMatch(a.TranscriptionId)))
                {
                    var targetId = context.ResolveSourceId(alias.TranscriptionId);
                    var target = context.FindSource(alias.TranscriptionId);
                    if (target?.IsFinished != true)
                    {
                        reasons.Add($"'{alias.TranscriptionId}' (alias of '{targetId}') is not done yet.");
                    }
                    if (target != null)
                    {
                        metadataProviders.Add(target);
                        aliasesUsed.Add((alias.TranscriptionId, targetId));
                    }
                }
            }

            return (
                metadataProviders.Distinct().ToArray(),
                reasons.Distinct().ToArray(),
                aliasesUsed);
        }

        /// <summary>
        /// The rules for a source written with the name of an alias ("alias,Metadata"), also for the source of the alias
        /// ("source,Metadata": the merge is done with the id of the real source). A rule already there for the source is kept.
        /// </summary>
        private static T WithAliasRules<T, TValue>(T rules, List<(string alias, string target)> aliasesUsed)
            where T : Dictionary<string, TValue>, new()
        {
            if (rules == null || aliasesUsed.Count == 0)
                return rules;
            var result = new T();
            foreach (var kvp in rules)
                result[kvp.Key] = kvp.Value;
            foreach (var (alias, target) in aliasesUsed.Distinct())
            {
                foreach (var kvp in rules.Where(r => r.Key.StartsWith(alias + ",", StringComparison.Ordinal)))
                {
                    var targetKey = target + kvp.Key.Substring(alias.Length);
                    if (!result.ContainsKey(targetKey))
                        result[targetKey] = kvp.Value;
                }
            }
            return result;
        }

        private static MergeRuleDictionary WithAliasRules(MergeRuleDictionary rules, List<(string alias, string target)> aliasesUsed)
            => WithAliasRules<MergeRuleDictionary, string>(rules, aliasesUsed);

        private static ValueTransformationRuleDictionary WithAliasRules(ValueTransformationRuleDictionary rules, List<(string alias, string target)> aliasesUsed)
            => WithAliasRules<ValueTransformationRuleDictionary, ValueTransformationRule>(rules, aliasesUsed);

        private static TimedItemWithMetadata[] MergeRawSources(
            ITiming[] timings,
            TimedItemWithMetadataCollection[] rawReferenceSources,
            MergeRuleDictionary mergeRules,
            ValueTransformationRuleDictionary valueTransformationRules)
        {
            if (timings == null)
                return null;

            var allItems = rawReferenceSources.SelectMany(t => t.GetItems()).ToList();
            var mergedItems = new List<TimedItemWithMetadata>();
            foreach (var timing in timings)
            {
                var metadata = new MetadataCollection();
                foreach (var rawReferenceSource in rawReferenceSources)
                {
                    var nbMerges = 0;
                    foreach (var item in rawReferenceSource.GetItems().Where(item => item.StartTime < timing.EndTime && item.EndTime > timing.StartTime))
                    {
                        allItems.Remove(item);
                        metadata.Merge(item.Metadata, mergeRules, valueTransformationRules, rawReferenceSource.Id, rawReferenceSource.PrivateMetadataNames);
                        nbMerges++;
                    }
                    if (nbMerges >= 2)
                    {
                        // TODO 
                        //throw new Exception("do we handle this differently? do we merge the values?");
                    }
                }
                mergedItems.Add(new TimedItemWithMetadata(timing.StartTime, timing.EndTime, metadata));
            }
            if (allItems.Count > 0)
            {
                // TODO Handle extra nodes
                //throw new Exception("TODO What to do with left over.");
            }
            return mergedItems
                .OrderBy(f => f.StartTime)
                .ToArray();
        }
    }
}