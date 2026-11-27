using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Inputs
{
    public class InputImportText : Infra.TrackProducer
    {
        [JsonProperty(Order = 20, Required = Required.Always)]
        public string FileSuffix { get; set; }

        [JsonProperty(Order = 21, Required = Required.Always)]
        public string MetadataProduced { get; set; }

        protected override string GetMetadataProduced() => this.MetadataProduced;
        protected override Infra.Language GetTrackLanguage(SubtitleGeneratorContext context) => context.Config.SourceLanguage;
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