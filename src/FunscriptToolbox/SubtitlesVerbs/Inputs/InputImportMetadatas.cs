using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Inputs
{
    [ConfigDoc("Imports a .srt next to the video (FileSuffix, ex. '.manual-input.srt', the file reviewed by the user): its subtitles, their timings, and the metadata " +
        "written in them ('{Name:Value}' lines, ex. {Speaker:...}, {GrabOnScreenText}). The scene waits until the file exists " +
        "(and, with ProcessOnlyWhenStringIsRemoved, until that text is removed from it: the sign that the review is done).",
        Advice = "With CanBeUpdated, the file is imported again at each run: a change in it is used by the next steps.")]
    public class InputImportMetadatas : Infra.TrackProducer
    {
        [ConfigDoc("The suffix of the file imported, next to the video (ex. \".manual-input.srt\"). A scene in several parts: one file by part (read as one).")]
        [JsonProperty(Order = 20, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [ConfigDoc("A text that the file must not contain anymore before it's imported (ex. a marker written in the file prepared for the review, removed by the user when done). null: imported as soon as it exists.")]
        [JsonProperty(Order = 21)]
        public string ProcessOnlyWhenStringIsRemoved { get; internal set; }

        protected override string GetMetadataProduced() => null;
        protected override Language GetTrackLanguage(SubtitleGeneratorContext context) => context.Config.SourceLanguage;
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