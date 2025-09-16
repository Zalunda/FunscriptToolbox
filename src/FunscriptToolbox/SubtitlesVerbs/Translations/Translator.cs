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
                    this.TargetLanguage);
                context.WIP.Translations.Add(translation);
            }
        }

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