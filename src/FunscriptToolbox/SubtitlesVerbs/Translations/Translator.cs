using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public abstract class Translator : SubtitleWorker
    {
        [ConfigDoc("The id of the translation produced (ex. \"translated-texts_maverick\"): the other steps use it (ex. in Metadatas.Sources), and an override file uses it to change this step.\n" +
            "The result is kept in the .wipsubtitles under this id: a new id = done again from the start.")]
        [JsonProperty(Order = 1, Required = Required.Always)]
        public string TranslationId { get; set; }

        [ConfigDoc("The language to translate to (ex. \"en\", \"fr\"): used in the prompts ([TranslationLanguage]).")]
        [JsonProperty(Order = 3)]
        public Language TargetLanguage { get; set; } = Language.FromString("en");

        [ConfigDoc("Metadata of this step that the next steps don't receive (ex. a field only useful to debug). ',' = several.")]
        [JsonProperty(Order = 6)]
        public string PrivateMetadataNames { get; set; }

        [ConfigDoc("Writes the result with all its metadata in '<video>.Worker.<id>.srt'.",
            Advice = "Useful to look at the result of a step in a subtitle editor.")]
        [JsonProperty(Order = 7)]
        public bool ExportMetadataSrt { get; set; } = false;

        [ConfigDoc("The first time the step is reached, the scene stops before doing it (to look at the result of the steps before it); the next time, it's done.")]
        [JsonProperty(Order = 8)]
        public bool StopOnFirstExecution { get; set; } = false;

        protected abstract string GetMetadataProduced();

        protected override string GetId() => $"{this.TranslationId}";
        protected override string GetWorkerTypeName() => "Translation";
        protected override string GetExecutionVerb() => "Translating";

        protected override void DescribeDataFlow(WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            flow.Output(DataKind.Translation, this.TranslationId);
            if (this.ExportMetadataSrt)
                flow.Output(DataKind.File, $".Worker.{this.TranslationId}.srt");
        }

        protected override bool IsFinished(SubtitleGeneratorContext context)
        {
            return context.WIP.Translations.Any(t => t.Id == this.GetId() && t.IsFinished);
        }

        protected override void EnsureDataObjectExists(SubtitleGeneratorContext context)
        {
            if (!context.WIP.Translations.Any(t => t.Id == this.GetId()))
            {
                var translation = new Translation(
                    this.TranslationId,
                    this.GetMetadataProduced(),
                    GetPrivateMetadataNamesArray(this.PrivateMetadataNames),
                    this.TargetLanguage);
                context.WIP.Translations.Add(translation);
            }
        }

        protected override void DoWork(SubtitleGeneratorContext context)
        {
            var translation = context.WIP.Translations.FirstOrDefault(t => t.Id == this.TranslationId);
            var importedTranslation = TryImportMetadatasSrt(context, translation);

            if (importedTranslation != null)
            {
                translation = importedTranslation;
            }
            else if (this.StopOnFirstExecution && !translation.HasStoppedOnFirstExecution)
            {
                context.WriteInfo("STOPPING to let user see the result of previous steps.");
                translation.HasStoppedOnFirstExecution = true;
                context.WIP.Save();
            }
            else
            {
                DoWorkInternal(context, translation);
            }
        }

        private Translation TryImportMetadatasSrt(
            SubtitleGeneratorContext context,
            Translation translation)
        {
            var virtualSubtitleFile = context.WIP.LoadVirtualSubtitleFile(
                $".Worker.{this.TranslationId}.import.srt");
            if (virtualSubtitleFile == null)
            {
                return null;
            }
            else
            {
                RenameOldTranslation(context, translation);

                var importedTranslation = new Translation(
                    this.TranslationId,
                    this.GetMetadataProduced(),
                    GetPrivateMetadataNamesArray(this.PrivateMetadataNames),
                    context.Config.SourceLanguage,
                    true,
                    ReadMetadataSubtitles(virtualSubtitleFile.Subtitles).
                        Select(f => new TranslatedItem(f.StartTime, f.EndTime, f.Metadata)));
                context.WIP.Translations.Add(importedTranslation);
                context.WIP.Save();

                return importedTranslation;
            }
        }

        private static void RenameOldTranslation(SubtitleGeneratorContext context, Translation translation)
        {
            int i = 2;
            string newId = $"{translation.Id}-OLD";
            while (context.WIP.Translations.Any(t => t.Id == newId))
            {
                newId = $"{translation.Id}-OLD{i++}";
            }
            translation.ChangeId(newId);
        }

        protected abstract void DoWorkInternal(SubtitleGeneratorContext context, Translation translation);

        protected override void AfterWork(SubtitleGeneratorContext context, bool wasAlreadyFinished)
        {
            var translation = context.WIP.Translations.FirstOrDefault(t => t.Id == GetId());
            if (translation != null && this.ExportMetadataSrt)
            {
                DoExportMetatadaSrt(context, translation, wasAlreadyFinished);
            }
        }

        protected override IEnumerable<string> GetAdditionalStatusLines(SubtitleGeneratorContext context)
        {
            var translation = context.WIP.Translations.First(t => t.Id == GetId());
            yield break;
        }
    }
}