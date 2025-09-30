using FunscriptToolbox.Core;
using Newtonsoft.Json;
using System;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    internal class TranscriberResetImportedMetadatas : Transcriber
    {
        [JsonProperty(Order = 20, Required = Required.Always)]
        public string SourceFileSuffix { get; set; }
        [JsonProperty(Order = 21, Required = Required.Always)]
        public string TranscriptionIdToBeUpdated { get; internal set; }
        [JsonProperty(Order = 22)]
        public string AddToFirstSubtitle { get; set; }

        protected override string GetMetadataProduced() => null;

        protected override bool IsPrerequisitesMet(SubtitleGeneratorContext context, out string reason)
        {
            if (context.WIP.LoadVirtualSubtitleFile(SourceFileSuffix) == null)
            {
                reason = $"Files '{SourceFileSuffix}' hasn't been created yet.";
                return false;
            }

            if (!context.Config.Workers.OfType<TranscriberImportMetadatas>().Any(f => f.TranscriptionId == this.TranscriptionIdToBeUpdated))
            {
                reason = $"Cannot find Transcriber '{TranscriptionIdToBeUpdated}' in config.";
                return false;
            }
            if (!context.WIP.Transcriptions.Any(f => f.Id == this.TranscriptionIdToBeUpdated && f.IsFinished))
            {
                reason = $"Transcription '{TranscriptionIdToBeUpdated}' not done yet.";
                return false;
            }

            reason = null;
            return true;
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Transcription transcription)
        {
            var transcriptionToBeUpdated = context.WIP.Transcriptions.FirstOrDefault(f => f.Id == this.TranscriptionIdToBeUpdated && f.IsFinished);
            transcriptionToBeUpdated.MarkAsNotFinished();

            var subtitleSource = context.WIP.LoadVirtualSubtitleFile(SourceFileSuffix);
            var firstSubtitle = subtitleSource.Subtitles.FirstOrDefault();
            if (firstSubtitle == null)
            {
                firstSubtitle = new Subtitle(TimeSpan.Zero, TimeSpan.FromSeconds(5), $"{this.AddToFirstSubtitle}");
                subtitleSource.Subtitles.Add(firstSubtitle);
            }
            else
            {
                subtitleSource.Subtitles[0] = new Subtitle(firstSubtitle.StartTime, firstSubtitle.EndTime, $"{this.AddToFirstSubtitle}\n{firstSubtitle.Text}");
            }
            subtitleSource.Save(
                context.WIP.ParentPath,
                context.Config.Workers.OfType<TranscriberImportMetadatas>().First(f => f.TranscriptionId == this.TranscriptionIdToBeUpdated).FileSuffix,
                context.SoftDelete);

            transcription.MarkAsFinished();
            context.WIP.Save();
        }
    }
}