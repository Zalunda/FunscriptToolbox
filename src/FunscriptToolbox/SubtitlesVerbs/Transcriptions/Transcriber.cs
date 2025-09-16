using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public abstract class Transcriber : SubtitleWorker
    {
        [JsonProperty(Order = 1, Required = Required.Always)]
        public string TranscriptionId { get; set; }

        [JsonProperty(Order = 6)]
        public bool ExportMetadataSrt { get; set; } = false;

        protected override string GetId() => this.TranscriptionId;
        protected override string GetWorkerTypeName() => "Transcription";
        protected override string GetExecutionVerb() => "Transcribing";
        protected abstract string GetMetadataProduced();

        protected override bool IsFinished(SubtitleGeneratorContext context)
        {
            return context.WIP.Transcriptions.Any(t => t.Id == this.TranscriptionId && t.IsFinished);
        }

        protected override void EnsureDataObjectExists(SubtitleGeneratorContext context)
        {
            if (!context.WIP.Transcriptions.Any(t => t.Id == this.TranscriptionId))
            {
                var transcription = new Transcription(
                    this.TranscriptionId,
                    this.GetMetadataProduced(),
                    context.Config.SourceLanguage);
                context.WIP.Transcriptions.Add(transcription);
            }
        }

        // DoWork

        protected override void AfterWork(SubtitleGeneratorContext context, bool wasAlreadyFinished)
        {
            var transcription = context.WIP.Transcriptions.FirstOrDefault(t => t.Id == this.TranscriptionId);
            if (transcription != null && this.ExportMetadataSrt)
            {
                DoExportMetatadaSrt(context, transcription, wasAlreadyFinished);
            }
        }

        protected override IEnumerable<string> GetAdditionalStatusLines(SubtitleGeneratorContext context)
        {
            var transcription = context.WIP.Transcriptions.First(t => t.Id == this.TranscriptionId);

            yield return $"Number of subtitles = {transcription.Items.Count}";
            yield return $"Total subtitles duration = {transcription.Items.Sum(f => f.Duration)}";
            yield return $"Detected Language = {transcription.Language.LongName}";
            foreach (var line in GetTranscriptionAnalysis(context, transcription))
            {
                yield return line;
            }
        }

        private IEnumerable<string> GetTranscriptionAnalysis(
            SubtitleGeneratorContext context,
            Transcription transcription)
        {
            var firstPerfectVadId = context.Config.Workers.OfType<TranscriberImportMetadatas>().FirstOrDefault(f => f.Enabled)?.TranscriptionId;
            var timings = context.WIP.Transcriptions.FirstOrDefault(t => t.Id == firstPerfectVadId && t.IsFinished)?.GetItems();
            if (timings != null && transcription.MetadataAlwaysProduced != null)
            {
                var nbEmptyItems = transcription.Items.Count(item => string.IsNullOrWhiteSpace(item.Metadata.Get(transcription.MetadataAlwaysProduced)));
                var suffixeEmptyItems = nbEmptyItems == 0 ? string.Empty : $" ({nbEmptyItems} are empty)";

                var analysis = transcription.GetAnalysis(timings);
                yield return $"ForcedTimings Analysis:";
                yield return $"   Number with transcription:    {analysis.NbTimingsWithTranscription}{suffixeEmptyItems}";
                yield return $"   Number without transcription: {analysis.TimingsWithoutItem.Length}";
                if (analysis.ExtraItems.Count > 0)
                {
                    yield return $"   Extra transcriptions:         {analysis.ExtraItems.Count}";
                }
            }
        }
    }
}