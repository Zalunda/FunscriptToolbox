using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Linq;
using System.Text;
using FunscriptToolbox.Core;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    internal class SubtitleOutputComplexSrt : SubtitleOutput
    {
        public SubtitleOutputComplexSrt()
        {

        }

        public override string Description => $"{base.Description}: {this.FileSuffix}";

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [JsonProperty(Order = 11)]
        public bool WaitForFinished { get; set; } = false;

        [JsonProperty(Order = 20, Required = Required.Always)]
        public MetadataAggregator Metadatas { get; set; }
        [JsonProperty(Order = 21)]
        public string TextSources { get; set; }
        [JsonProperty(Order = 22)]
        public string SkipWhenTextSourcesAreIdentical { get; set; }

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            var aggregate = this.Metadatas.Aggregate(context);

            if (this.WaitForFinished)
            {
                if (aggregate.IsPrerequisitesMetWithTimings(out reason) == false)
                {
                    return false;
                }

                var (_, reasons) = this.Metadatas.GetOthersSources(context, this.TextSources?.Split(',').Select(f => f.Trim()).ToArray());
                if (reasons.Any() == true)
                {
                    reason = (reasons.Length > 0)
                            ? "\n" + string.Join("\n", reasons)
                            : reasons.First();
                    return false;
                }
            }
            else
            {
                if (aggregate.IsPrerequisitesMetOnlyTimings(out reason) == false)
                {
                    return false;
                }
            }

            reason = null;
            return true;
        }

        public override void CreateOutput(
            SubtitleGeneratorContext context)
        {
            var virtualSubtitleFile = context.WIP.CreateVirtualSubtitleFile();

            var aggregation = this.Metadatas.Aggregate(context);
            var timings = aggregation.ReferenceTimingsWithMetadata;

            var textSourceIds = this.TextSources?.Split(',').Select(f => f.Trim()).ToArray() ?? Array.Empty<string>();
            var (textSourcesRaw, _) = this.Metadatas.GetOthersSources(context, textSourceIds);
            var textSources = textSourcesRaw.Select(osr => osr.GetAnalysis(timings)).ToArray();

            var metadataSourcesRaw = this.Metadatas.GetSources(context);
            var metadataSources = metadataSourcesRaw.Select(osr => osr.GetAnalysis(timings)).ToArray();

            var sourcesToCompareIds = this.SkipWhenTextSourcesAreIdentical?.Split(',').Select(f => f.Trim()).ToHashSet();

            foreach (var timing in timings)
            {
                var sb = new StringBuilder();
                var uniqueValuesToCompare = new HashSet<string>();
                int sourcesWithValueCount = 0;

                // 1. Append Text from specified sources & collect values for skip-check in a single loop
                foreach (var textSource in textSources)
                {
                    bool containsValue = false;
                    if (textSource.TimingsWithOverlapItems.TryGetValue(timing, out var overlaps))
                    {
                        foreach (var overlap in overlaps)
                        {
                            var text = overlap.Item.Metadata.Get(textSource.Container.MetadataAlwaysProduced);
                            if (text == null) continue;

                            // Action A: Always build the text portion of the subtitle
                            sb.AppendLine($"[{textSource.Container.Id}] {text}");

                            // Action B: If this source is targeted, collect its value for the skip-check
                            if (sourcesToCompareIds?.Contains(textSource.Container.Id) == true)
                            {
                                uniqueValuesToCompare.Add(text);
                                containsValue = true;
                            }
                        }
                    }
                    sourcesWithValueCount += containsValue ? 1 : 0;
                }

                // Decision Point: After gathering all text, check if we should skip this timing
                if (sourcesToCompareIds?.Any() == true && sourcesWithValueCount > 1 && uniqueValuesToCompare.Count == 1)
                {
                    continue; // The values were identical; skip to the next timing.
                }

                sb.AppendLine("------- Metadatas -------");

                // 2. Append Metadatas from all sources in the aggregator
                foreach (var metadataSource in metadataSources)
                {
                    if (metadataSource.TimingsWithOverlapItems.TryGetValue(timing, out var overlaps))
                    {
                        foreach (var overlap in overlaps)
                        {
                            foreach (var metadata in overlap.Item.Metadata.OrderBy(kvp => kvp.Key))
                            {
                                sb.AppendLine($"[{metadataSource.Container.Id}] {{{metadata.Key}:{metadata.Value}}}");
                            }
                        }
                    }
                }

                if (sb.Length > 0)
                {
                    virtualSubtitleFile.Subtitles.Add(
                        new Subtitle(timing.StartTime,
                        timing.EndTime,
                        sb.ToString().TrimEnd()));
                }
            }


            virtualSubtitleFile.Save(
                context.WIP.ParentPath,
                this.FileSuffix,
                context.SoftDelete);
        }
    }
}