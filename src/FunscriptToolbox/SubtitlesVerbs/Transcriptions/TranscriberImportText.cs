using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberImportText : Transcriber
    {
        [JsonProperty(Order = 20, Required = Required.Always)]
        public string FileSuffix { get; set; }

        [JsonProperty(Order = 21, Required = Required.Always)]
        public string MetadataProduced { get; set; }

        protected override string GetMetadataProduced() => this.MetadataProduced;
        protected override string GetExecutionVerb() => "Importing";

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            var fullpath = context.WIP.BaseFilePath + this.FileSuffix;
            if (!File.Exists(fullpath))
            {
                reason = $"File '{Path.GetFileName(fullpath)}' does not exists yet.";
                context.AddUserTodo($"Create file '{Path.GetFileName(fullpath)}'.");
                return false;
            }

            reason = null;
            return true;
        }

        protected override void DoWork(SubtitleGeneratorContext context)
        {
            var transcription = context.WIP.Transcriptions.FirstOrDefault(t => t.Id == this.TranscriptionId);
            var fullpath = context.WIP.BaseFilePath + this.FileSuffix;
            var subtitleFile = SubtitleFile.FromSrtFile(fullpath);
            transcription.Items.Clear();
            transcription.Items.AddRange(
                subtitleFile.Subtitles
                    .Select(subtitle => new TranscribedItem(subtitle.StartTime, subtitle.EndTime, MetadataCollection.CreateSimple(this.MetadataProduced, subtitle.Text))));
            transcription.MarkAsFinished();

            context.WIP.Save();
        }
    }
}