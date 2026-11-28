using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Inputs
{
    [ConfigDoc("Imports a .srt next to the video (FileSuffix): its subtitles and their text, as the metadata MetadataProduced (ex. subtitles finished by hand, " +
        "to compare with the result of the AI). The scene waits until the file exists.")]
    public class InputImportText : Infra.TrackProducer
    {
        [ConfigDoc("The suffix of the .srt imported, next to the video (what follows the name of the video, ex. \".jp.srt\"). A scene in several parts: one file by part (read as one).")]
        [JsonProperty(Order = 20, Required = Required.Always)]
        public string FileSuffix { get; set; }

        [ConfigDoc("The name of the metadata that receives the text of each subtitle (ex. \"VoiceText\").")]
        [JsonProperty(Order = 21, Required = Required.Always)]
        public string MetadataProduced { get; set; }

        protected override string GetMetadataProduced() => this.MetadataProduced;
        protected override Infra.Language GetTrackLanguage(SubtitleGeneratorContext context) => context.Config.SourceLanguage;
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

            reason = null;
            return true;
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Track transcription)
        {
            var subtitleFile = context.WIP.LoadVirtualSubtitleFile(this.FileSuffix);

            transcription.Items.Clear();
            if (subtitleFile != null)
            {
                transcription.Items.AddRange(
                    subtitleFile.Subtitles
                    .Select(subtitle => new TimedItemWithMetadata(subtitle.StartTime, subtitle.EndTime, MetadataCollection.CreateSimple(this.MetadataProduced, subtitle.Text))));
            }
            transcription.MarkAsFinished();
            context.WIP.Save();
        }
    }
}