using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Inputs
{
    public class InputImportMetadatas : Infra.TrackProducer
    {
        [JsonProperty(Order = 20, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [JsonProperty(Order = 21)]
        public string ProcessOnlyWhenStringIsRemoved { get; internal set; }

        protected override string GetMetadataProduced() => null;
        protected override Language GetTrackLanguage(SubtitleGeneratorContext context) => context.Config.SourceLanguage;
        protected override string GetDefaultProduces() => "Import";
        protected override string GetDefaultVerb() => "Importing";

        protected override void DescribeDataFlow(Infra.WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            flow.Input(Infra.DataKind.File, this.FileSuffix, "imported");
        }

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context, 
            out string reason)
        {
            var subtitleFile = context.WIP.LoadVirtualSubtitleFile(this.FileSuffix);
            if (subtitleFile == null)
            {
                reason = $"File(s) '{this.FileSuffix}' does not exists yet.";
                context.AddUserTodo($"Create file '{this.FileSuffix}'.");
                return false;
            }

            if (!string.IsNullOrEmpty(ProcessOnlyWhenStringIsRemoved) 
                && subtitleFile.Subtitles.Any(s => s.Text.Contains(ProcessOnlyWhenStringIsRemoved)))
            {
                reason = $"File '{this.FileSuffix}' requires user revision.";
                context.AddUserTodo($"Please edit '{this.FileSuffix}' and remove the text '{ProcessOnlyWhenStringIsRemoved}'.");
                return false;
            }

            reason = null;
            return true;
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Track transcription)
        {
            var subtitleFile = context.WIP.LoadVirtualSubtitleFile(this.FileSuffix);

            transcription.Items.Clear();
            if (subtitleFile != null)
            {
                // Subtitles past the end of the video are ignored, the ones that overlap the end are cut
                // (not done when the duration is not known).
                var videoDuration = context.WIP.TimelineMap.Duration;
                var nbRemoved = 0;
                var nbCut = 0;
                foreach (var item in ReadMetadataSubtitles(subtitleFile.Subtitles))
                {
                    var endTime = item.EndTime;
                    if (videoDuration > TimeSpan.Zero)
                    {
                        if (item.StartTime >= videoDuration)
                        {
                            nbRemoved++;
                            continue;
                        }
                        if (endTime > videoDuration)
                        {
                            endTime = videoDuration;
                            nbCut++;
                        }
                    }
                    transcription.Items.Add(new TimedItemWithMetadata(item.StartTime, endTime, item.Metadata));
                }

                if (nbRemoved > 0 || nbCut > 0)
                {
                    context.WriteInfo($"  '{this.FileSuffix}': {nbRemoved} subtitle(s) after the end of the video ({videoDuration:hh\\:mm\\:ss\\.fff}) ignored, {nbCut} cut.", ConsoleColor.Yellow);
                }
            }
            transcription.MarkAsFinished();
            context.WIP.Save();
        }
    }
}