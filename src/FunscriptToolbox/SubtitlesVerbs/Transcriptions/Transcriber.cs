using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public abstract class Transcriber : SubtitleWorker
    {
        [ConfigDoc("The id of the transcription produced (ex. \"full-ai\"): the other steps use it (ex. in Metadatas.Sources), and an override file uses it to change this step.\n" +
            "The result is kept in the .wipsubtitles under this id: a new id = done again from the start.")]
        [JsonProperty(Order = 1, Required = Required.Always)]
        public string TranscriptionId { get; set; }

        [ConfigDoc("Metadata of this step that the next steps don't receive (ex. \"TimingTrace\", only useful to debug). ',' = several.")]
        [JsonProperty(Order = 6)]
        public string PrivateMetadataNames { get; set; }

        [ConfigDoc("Writes the result with all its metadata in '<video>.Worker.<id>.srt'. A '<video>.Worker.<id>.import.srt' (edited) is imported instead of doing the step " +
            "(the old result is kept as '<id>-OLD').",
            Advice = "Useful to look at the result of a step in a subtitle editor, or to fix it by hand.")]
        [JsonProperty(Order = 7)]
        public bool ExportMetadataSrt { get; set; } = false;

        [ConfigDoc("The first time the step is reached, the scene stops before doing it (to look at the result of the steps before it); the next time, it's done.")]
        [JsonProperty(Order = 8)]
        public bool StopOnFirstExecution { get; set; } = false;

        protected override string GetId() => this.TranscriptionId;
        protected override string GetWorkerTypeName() => "Transcription";
        protected override string GetExecutionVerb() => "Transcribing";
        protected abstract string GetMetadataProduced();

        protected override void DescribeDataFlow(WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            flow.Output(DataKind.Transcription, this.TranscriptionId);
            if (this.ExportMetadataSrt)
                flow.Output(DataKind.File, $".Worker.{this.TranscriptionId}.srt");
        }

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
                    GetPrivateMetadataNamesArray(this.PrivateMetadataNames),
                    context.Config.SourceLanguage) ;
                context.WIP.Transcriptions.Add(transcription);
            }
        }

        protected override void DoWork(SubtitleGeneratorContext context)
        {
            var transcription = context.WIP.Transcriptions.FirstOrDefault(t => t.Id == this.TranscriptionId);
            var importedTranscription = TryImportMetadatasSrt(context, transcription);

            if (importedTranscription != null)
            {
                transcription = importedTranscription;
            }
            else if (this.StopOnFirstExecution && !transcription.HasStoppedOnFirstExecution)
            {
                context.WriteInfo("STOPPING to let user see the result of previous steps.");
                transcription.HasStoppedOnFirstExecution = true;
                context.WIP.Save();
            }
            else
            {
                DoWorkInternal(context, transcription);
            }
        }

        protected abstract void DoWorkInternal(SubtitleGeneratorContext context, Transcription transcription);

        private Transcription TryImportMetadatasSrt(
            SubtitleGeneratorContext context,
            Transcription transcription)
        {
            var virtualSubtitleFile = context.WIP.LoadVirtualSubtitleFile(
                $".Worker.{this.TranscriptionId}.import.srt");
            if (virtualSubtitleFile == null)
            {
                return null;
            }
            else
            {
                RenameOldTranscription(context, transcription);

                var importedTranscription = new Transcription(
                    this.TranscriptionId,
                    this.GetMetadataProduced(),
                    GetPrivateMetadataNamesArray(this.PrivateMetadataNames),
                    context.Config.SourceLanguage,
                    true,
                    ReadMetadataSubtitles(virtualSubtitleFile.Subtitles).
                        Select(f => new TranscribedItem(f.StartTime, f.EndTime, f.Metadata)));
                context.WIP.Transcriptions.Add(importedTranscription);
                context.WIP.Save();

                return importedTranscription;
            }
        }

        private static void RenameOldTranscription(SubtitleGeneratorContext context, Transcription transcription)
        {
            int i = 2;
            string newId = $"{transcription.Id}-OLD";
            while (context.WIP.Transcriptions.Any(t => t.Id == newId))
            {
                newId = $"{transcription.Id}-OLD{i++}";
            }
            transcription.ChangeId(newId);
        }

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