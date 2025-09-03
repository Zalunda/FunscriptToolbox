using FunscriptToolbox.Core;
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

        protected override void Transcribe(
            SubtitleGeneratorContext context,
            Transcription transcription)
        {
            var fullpath = context.WIP.BaseFilePath + this.FileSuffix;
            var subtitleFile = SubtitleFile.FromSrtFile(fullpath);
            transcription.Items.Clear();
            transcription.Items.AddRange(
                ReadMetadataFromSrt(fullpath).
                    Select(f => new TranscribedItem(f.StartTime, f.EndTime, f.Metadata)));
            transcription.MarkAsFinished();
            context.WIP.Save();
        }
    }
}