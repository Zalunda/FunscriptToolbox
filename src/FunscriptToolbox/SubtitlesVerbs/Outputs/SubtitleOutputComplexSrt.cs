using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Linq;
using System.Text;
using FunscriptToolbox.Core;
using System;

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

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            var aggregate = this.Metadatas.Aggregate(context);
            if (aggregate.IsPrerequisitesMetWithTimings(out reason) == false)
            {
                return false;
            }

            if (this.WaitForFinished)
            {
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
            var subtitleFile = new SubtitleFile();
            var aggregation = this.Metadatas.Aggregate(context);
            var timings = aggregation.ReferenceTimingsWithMetadata;

            var textSourceIds = this.TextSources?.Split(',').Select(f => f.Trim()).ToArray() ?? Array.Empty<string>();
            var (textSourcesRaw, _) = this.Metadatas.GetOthersSources(context, textSourceIds);
            var textSources = textSourcesRaw.Select(osr => osr.GetAnalysis(timings)).ToArray();

            var metadataSourcesRaw = this.Metadatas.GetSources(context);
            var metadataSources = metadataSourcesRaw.Select(osr => osr.GetAnalysis(timings)).ToArray();

            foreach (var timing in timings)
            {
                var sb = new StringBuilder();

                // 1. Append Text from specified sources
                foreach (var textSource in textSources)
                {
                    if (textSource.TimingsWithOverlapItems.TryGetValue(timing, out var overlaps))
                    {
                        foreach (var overlap in overlaps)
                        {
                            var text = overlap.Item.Metadata.Get(textSource.Container.MetadataAlwaysProduced);
                            sb.AppendLine($"[{textSource.Container.Id}] {text}");
                        }
                    }
                }

                // 2. Append Metadata separator and header
                sb.AppendLine("------- Metadatas -------");

                // 3. Append Metadatas from all sources in the aggregator
                foreach (var metadataSource in metadataSources)
                {
                    if (metadataSource.TimingsWithOverlapItems.TryGetValue(timing, out var overlaps))
                    {
                        foreach (var overlap in overlaps)
                        {
                            foreach (var metadata in overlap.Item.Metadata.OrderBy(kvp => kvp.Key))
                            {
                                sb.AppendLine($"[{metadataSource.Container.Id}] {metadata.Key}:{metadata.Value}");
                            }
                        }
                    }
                }

                subtitleFile.Subtitles.Add(
                    new Subtitle(timing.StartTime,
                    timing.EndTime,
                    sb.ToString().TrimEnd()));
            }

            var filename = context.WIP.BaseFilePath + this.FileSuffix;
            context.SoftDelete(filename);
            subtitleFile.SaveSrt(filename);
        }
    }
}