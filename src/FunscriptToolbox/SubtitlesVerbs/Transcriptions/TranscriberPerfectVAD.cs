using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberPerfectVAD : Transcriber
    {
        public override bool CanBeUpdated => true;

        public string FileSuffix { get; set; } = ".perfect-vad.srt";

        protected override string GetMetadataProduced() => null;

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context, 
            out string reason)
        {
            if (context.WIP.LoadVirtualSubtitleFile(this.FileSuffix).Subtitles.Count == 0)
            {
                reason = $"Files '{this.FileSuffix}' does not exists yet.";
                context.AddUserTodo($"Create file '{this.FileSuffix}'.");
                return false;
            }

            reason = null;
            return true;
        }

        protected override void Transcribe(
            SubtitleGeneratorContext context,
            Transcription transcription)
        {
            var subtitleFile = context.WIP.LoadVirtualSubtitleFile(this.FileSuffix);

            transcription.Items.Clear();
            transcription.Items.AddRange(
                ReadMetadataSubtitles(subtitleFile.Subtitles)
                .Select(item => new TranscribedItem(item.StartTime, item.EndTime, item.Metadata)));
            transcription.MarkAsFinished();
            context.WIP.Save();
        }
    }
}