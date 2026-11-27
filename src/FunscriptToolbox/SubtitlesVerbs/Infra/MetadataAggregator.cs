using FunscriptToolbox.SubtitlesVerbs.Processors;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class MetadataAggregator
    {
        public MetadataAggregator()
        {
        }

        [ConfigDoc("The id of the step whose subtitles give the timings (usually \"timings\"): one node per subtitle of this source; the metadata of the Sources are attached to these nodes.")]
        [JsonProperty(Order = 1)]
        public string TimingsSource { get; set; }
        [ConfigDoc("The ids of the steps whose metadata are attached to each node, separated by ',' (ex. \"singlevad-ai-refined,on-screen-texts,speakers,manual-input\"). " +
            "A subtitle of a source that overlaps a node gives its metadata to that node. An id that no step has is an error.\n" +
            "When two sources have the same metadata, the last one in the list wins.")]
        [JsonProperty(Order = 2)]
        public string Sources { get; set; }
        [ConfigDoc("Renames (or removes, with null) metadata of the sources before they're merged: { \"<metadata>\": \"<new name>\" } for every source, " +
            "or { \"<source id>,<metadata>\": \"<new name>\" } for one source (the first form wins when both are there).\n" +
            "Ex. { \"TranslatedText\": \"OriginalTranslatedText\" }: the translation of a source kept under another name, so that the AI can refine it without it being replaced by its own answer.")]
        [JsonProperty(Order = 3)]
        public MergeRuleDictionary MergeRules { get; set; }
        [ConfigDoc("Changes the values of metadata of the sources before they're merged: { \"<metadata>\": { \"Pattern\": ..., \"Replacement\": ... } } for every source, " +
            "or { \"<source id>,<metadata>\": { ... } } for one source (the first form wins when both are there).\n" +
            "Ex. { \"TranslationAnalysis\": { \"Pattern\": \"^Option \\\\d.*$\", \"Replacement\": \"\" } }: the lines 'Option n...' removed.")]
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
        /// A source (ex. "full-whisper") is the id of a transcription / translation, exactly (no wildcard: every dependency is
        /// known, so a missing one can be reported). (also used by the flow graph of the web UI: the same rule as the run)
        /// </summary>
        internal static bool IsSourceMatch(string source, string id)
            => string.Equals(source, id, StringComparison.Ordinal);

        /// <summary>A clear error for a source that can't be used: a wildcard (not supported anymore), or an id that no step has (ex. a typo).</summary>
        private static void ValidateSource(SubtitleGeneratorContext context, string source)
        {
            if (source.Contains("*"))
                throw new InvalidOperationException(
                    $"'Sources': '{source}': the wildcard '*' is not supported anymore. List the ids of the steps, separated by ','.");
            var known = context.Config.Workers.Any(w => (w is TrackProducer || w is TrackAlias) && IsSourceMatch(source, w.Id));
            if (!known)
                throw new InvalidOperationException(
                    $"'Sources': no step has the id '{source}' (a typo, or a step that was removed / renamed?).");
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
        /// (see <see cref="TrackAlias"/>), the source of the alias is then used (once, even if it's also matched by its own id).
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

            var aliases = TrackAlias.GetAliases(context.Config);
            var aliasIds = new HashSet<string>(aliases.Select(a => a.Id));

            foreach (var rule in sources ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(rule))
                    continue;
                ValidateSource(context, rule);

                foreach (var producer in context.Config.Workers.OfType<TrackProducer>()
                        .Where(t => IsSourceMatch(rule, t.Id)))
                {
                    var track = context.WIP.Tracks.FirstOrDefault(t => t.Id == producer.Id);
                    if (producer.Enabled && track?.IsFinished != true)
                    {
                        reasons.Add($"'{producer.Id}' is not done yet.");
                    }
                }

                metadataProviders.AddRange(context.WIP.Tracks.Where(t => IsSourceMatch(rule, t.Id) && !aliasIds.Contains(t.Id)));

                // The aliases: their source
                foreach (var alias in aliases.Where(a => IsSourceMatch(rule, a.Id)))
                {
                    var targetId = context.ResolveSourceId(alias.Id);
                    var target = context.FindSource(alias.Id);
                    if (target?.IsFinished != true)
                    {
                        reasons.Add($"'{alias.Id}' (alias of '{targetId}') is not done yet.");
                    }
                    if (target != null)
                    {
                        metadataProviders.Add(target);
                        aliasesUsed.Add((alias.Id, targetId));
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