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
    [ConfigDoc("Writes a .srt next to the video (FileSuffix): the subtitles of a track (Metadatas.TimingsSource, or the first of TextSources) with the text of the TextSources (several: each one under its name), the metadata of the sources when asked (ex. a debug file), the subtitles to inject (ex. a credit at the start). A scene in several parts: one .srt per part (SaveFullFileToo: also one of the whole scene).")]
    internal class OutputSrt : SubtitleOutput
    {
        public OutputSrt()
        {

        }


        [ConfigDoc("The end of the name of the file written next to the video (ex. \".final-ai.srt\" => '<video>.final-ai.srt'; \".srt\" => '<video>.srt', used by the players).")]
        [JsonProperty(Order = 5, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [ConfigDoc("Writes the file only when all the sources are finished (false: written with what is there, and written again later).")]
        [JsonProperty(Order = 10)]
        public bool WaitForFinished { get; set; } = false;

        [ConfigDoc("The subtitles of the file (TimingsSource) and the steps whose metadata are written in them (Sources).")]
        [JsonProperty(Order = 15)]
        public MetadataAggregator Metadatas { get; set; }

        [ConfigDoc("Writes the metadata of each source in the subtitles (a '------- Metadatas -------' section, '[source] {Name:Value}'), ex. for a debug file.")]
        [JsonProperty(Order = 16)]
        public bool UseBySourceMetadatas { get; set; } = true;

        [ConfigDoc("The ids of the steps whose text is written in the subtitles, separated by ',' (ex. \"conformed-texts\").")]
        [JsonProperty(Order = 17)]
        public string TextSources { get; set; }
        [ConfigDoc("The metadata used as the text of the sources (null: the main metadata of each source, ex. \"TranslatedText\" for a translation).")]
        [JsonProperty(Order = 18)]
        public string MetadataToUse { get; set; }
        [ConfigDoc("The old name of TextSources (still read in the old files): use TextSources.")]
        public string WorkerId // For backward compatibility
        {
            set { TextSources = value; }
        }

        [ConfigDoc("Added to the end of every subtitle, for the comfort of reading (never over the start of the next subtitle).")]
        [JsonProperty(Order = 20)]
        public TimeSpan ExpandSubtileDuration { get; set; } = TimeSpan.Zero;
        [ConfigDoc("Then, a subtitle still shorter than this is made this long (never over the start of the next subtitle).")]
        [JsonProperty(Order = 21)]
        public TimeSpan MinimumSubtitleDuration { get; set; } = TimeSpan.Zero;

        [ConfigDoc("Text added to the first subtitle of the file (ex. a line for the user to fill).")]
        [JsonProperty(Order = 22)]
        public string AddToFirstSubtitle { get; set; } = string.Empty;
        [ConfigDoc("Fixed subtitles added to the file (ex. the credits at the start of the video).",
            Advice = "Put your name / your credit line here (in your .override.user.config).")]
        [JsonProperty(Order = 23)]
        public SubtitleToInject[] SubtitlesToInject { get; set; }

        [ConfigDoc("Two ids of TextSources, separated by ',': a subtitle where their texts are the same is left out (only the differences are written, ex. a file of what you changed).")]
        [JsonProperty(Order = 24)]
        public string SkipWhenTextSourcesAreIdentical { get; set; }
        [ConfigDoc("For a scene made of several videos (.vseq): also writes one file for the whole scene ('<scene><FileSuffix>'), not only one per video.")]
        [JsonProperty(Order = 25)]
        public bool SaveFullFileToo { get; set; } = false;

        protected override void DescribeDataFlow(WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            flow.InputMetadatas(this.Metadatas);
            flow.InputSources(this.TextSources, "text");
            flow.Output(DataKind.File, this.FileSuffix);
        }

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
                            var text = overlap.Item.Metadata.Get(this.MetadataToUse ?? textSource.Container.MetadataAlwaysProduced);
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
                if (UseBySourceMetadatas)
                {
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
                }
                else
                {
                    foreach (var metadata in timing.Metadata)
                    {
                        sb.AppendLine($"{{{metadata.Key}:{metadata.Value}}}");
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