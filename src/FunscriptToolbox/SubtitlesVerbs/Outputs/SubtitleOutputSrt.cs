using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Linq;
using System.Text;
using FunscriptToolbox.Core;
using System;
using System.Collections.Generic;
using System.IO;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    internal class SubtitleOutputSrt : SubtitleOutput
    {
        public SubtitleOutputSrt()
        {

        }


        [JsonProperty(Order = 5, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [JsonProperty(Order = 10)]
        public bool WaitForFinished { get; set; } = false;

        [JsonProperty(Order = 15)]
        public MetadataAggregator Metadatas { get; set; }
        [JsonProperty(Order = 16)]
        public string TextSources { get; set; }
        public string WorkerId // For backward compatibility
        {
            set { TextSources = value; }
        }

        [JsonProperty(Order = 20)]
        public TimeSpan MinimumSubtitleDuration { get; set; } = TimeSpan.Zero;
        [JsonProperty(Order = 21)]
        public TimeSpan ExpandSubtileDuration { get; set; } = TimeSpan.Zero;

        [JsonProperty(Order = 22)]
        public string AddToFirstSubtitle { get; set; } = string.Empty;
        [JsonProperty(Order = 23)]
        public SubtitleToInject[] SubtitlesToInject { get; set; }

        [JsonProperty(Order = 24)]
        public string SkipWhenTextSourcesAreIdentical { get; set; }
        [JsonProperty(Order = 25)]
        public bool SaveFullFileToo { get; set; } = false;


        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            var textSourceIds = this.TextSources?.Split(',')
                .Select(f => f.Trim())
                .Where(f => !string.IsNullOrEmpty(f))
                .ToArray() ?? Array.Empty<string>();

            // Normalize state
            if (this.Metadatas == null && textSourceIds.Any())
            {
                this.Metadatas = new MetadataAggregator()
                {
                    TimingsSource = textSourceIds.First(),
                    Sources = "" // Empty string ensures no metadata analysis is appended
                };
            }

            if (this.Metadatas == null)
            {
                // If there's no Metadata and no TextSources, the only valid reason to proceed is if we have manual text to inject
                if (string.IsNullOrEmpty(this.AddToFirstSubtitle))
                {
                    reason = "TextSources or Metadatas.TimingsSource must be set.";
                    return false;
                }

                reason = null;
                return true;
            }

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

        protected override bool IsFinished(SubtitleGeneratorContext context)
        {
            if (context.WIP.LoadVirtualSubtitleFile(this.FileSuffix) == null)
                return false;

            return (this.SaveFullFileToo && context.WIP.TimelineMap.Segments.Length > 1)
                ? File.Exists(Path.Combine(context.WIP.BaseFilePath + this.FileSuffix))
                : true;
        }

        protected override void DoWork(
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
            string addToNextSubtitle = string.IsNullOrEmpty(this.AddToFirstSubtitle) ? null : "\n" + this.AddToFirstSubtitle;

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
                            sb.AppendLine((textSources.Length > 1)
                                    ? $"[{textSource.Container.Id}] {text}"
                                    : text);

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

                // 2. Append Metadatas from all sources in the aggregator
                if (metadataSources.Length > 0)
                {
                    sb.AppendLine("------- Metadatas -------");

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
                }

                string subtitleText = sb.ToString().TrimEnd();

                if (addToNextSubtitle != null)
                {
                    subtitleText = (subtitleText + addToNextSubtitle).TrimStart();
                    addToNextSubtitle = null;
                }

                virtualSubtitleFile.Subtitles.Add(
                    new Subtitle(timing.StartTime, timing.EndTime, subtitleText));
            }

            if (addToNextSubtitle != null)
            {
                virtualSubtitleFile.Subtitles.Add(new Subtitle(TimeSpan.Zero, TimeSpan.FromSeconds(5), addToNextSubtitle.TrimStart()));
            }

            // Apply SaveFullFileToo
            if (this.SaveFullFileToo && context.WIP.TimelineMap.Segments.Length > 1)
            {
                var newFullFile = new SubtitleFile();
                // Copy freshly generated items prior to expansion
                newFullFile.Subtitles.AddRange(
                    virtualSubtitleFile.Subtitles.Select(item =>
                        new Subtitle(item.StartTime, item.EndTime, item.Text)));

                VirtualSubtitleFile.InjectSubtitleInFile(newFullFile, this.SubtitlesToInject, TimeSpan.Zero);
                newFullFile.ExpandTiming(this.MinimumSubtitleDuration, this.ExpandSubtileDuration);
                newFullFile.SaveSrt(
                    Path.Combine(
                        context.WIP.BaseFilePath + this.FileSuffix));
            }

            // Save part files
            virtualSubtitleFile.ExpandTiming(this.MinimumSubtitleDuration, this.ExpandSubtileDuration);
            virtualSubtitleFile.Save(
                context.WIP.ParentPath,
                this.FileSuffix,
                context.SoftDelete,
                this.SubtitlesToInject);
        }
    }
}