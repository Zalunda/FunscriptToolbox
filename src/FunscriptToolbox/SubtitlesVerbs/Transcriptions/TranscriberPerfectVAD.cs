using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberPerfectVAD : Transcriber
    {
        public override bool CanBeUpdated => true;

        public string FileSuffix { get; set; } = ".perfect-vad.srt";

        public string MetadataExtractionRegex { get; set; } = @"{(?<name>[^}:]*)(\:(?<value>[^}]*))?}";

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context, 
            out string reason)
        {
            var fullpath = context.CurrentBaseFilePath + this.FileSuffix;
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
            var fullpath = context.CurrentBaseFilePath + this.FileSuffix;
            var subtitleFile = SubtitleFile.FromSrtFile(fullpath);
            transcription.Items.Clear();
            transcription.Items.AddRange(
                subtitleFile
                .Subtitles
                .Select(subtitle => new TranscribedItem(
                    subtitle.StartTime,
                    subtitle.EndTime,
                    metadata: new MetadataCollection(
                        Regex
                        .Matches(subtitle.Text, this.MetadataExtractionRegex)
                        .Cast<Match>()
                        .ToDictionary(
                            match => match.Groups["name"].Value,
                            match => match.Groups["value"].Success ? match.Groups["value"].Value : string.Empty)))));
            transcription.MarkAsFinished();
            context.CurrentWipsub.Save();

            SaveDebugSrtIfVerbose(context, transcription);
        }
    }
}