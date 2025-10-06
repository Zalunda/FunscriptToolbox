using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public abstract class Translator : SubtitleWorker
    {
        [JsonProperty(Order = 1, Required = Required.Always)]
        public string TranslationId { get; set; }

        [JsonProperty(Order = 3)]
        public Language TargetLanguage { get; set; } = Language.FromString("en");

        [JsonProperty(Order = 6)]
        public string PrivateMetadataNames { get; set; }

        [JsonProperty(Order = 7)]
        public bool ExportMetadataSrt { get; set; } = false;

        protected abstract string GetMetadataProduced();

        protected override string GetId() => $"{this.TranslationId}";
        protected override string GetWorkerTypeName() => "Translation";
        protected override string GetExecutionVerb() => "Translating";

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

            if (importedTranslation == null)
            {
                DoWorkInternal(context, translation);
            }
            else
            {
                translation = importedTranslation;
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