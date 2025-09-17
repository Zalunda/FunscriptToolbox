using Newtonsoft.Json;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberImportMetadatas : Transcriber
    {
        [JsonProperty(Order = 20, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [JsonProperty(Order = 21)]
        public string ProcessOnlyWhenStringIsRemoved { get; internal set; }

        protected override string GetMetadataProduced() => null;
        protected override string GetExecutionVerb() => "Importing";

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

        protected override void DoWork(SubtitleGeneratorContext context)
        {
            var transcription = context.WIP.Transcriptions.FirstOrDefault(t => t.Id == this.TranscriptionId);
            var subtitleFile = context.WIP.LoadVirtualSubtitleFile(this.FileSuffix);

            transcription.Items.Clear();
            if (subtitleFile != null)
            {
                transcription.Items.AddRange(
                    ReadMetadataSubtitles(subtitleFile.Subtitles)
                    .Select(item => new TranscribedItem(item.StartTime, item.EndTime, item.Metadata)));
            }
            transcription.MarkAsFinished();
            context.WIP.Save();
        }
    }
}