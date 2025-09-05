using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System.Diagnostics;
using System;
using System.Linq;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public abstract class Translator : SubtitleWorker
    {
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        [JsonProperty(Order = 2, Required = Required.Always)]
        public string TranslationId { get; set; }

        [JsonProperty(Order = 3, Required = Required.Always)]
        public string TranscriptionId { get; set; }

        [JsonProperty(Order = 3)]
        public Language TargetLanguage { get; set; } = Language.FromString("en");
        [JsonIgnore]
        public string FullId => $"{this.TranscriptionId}_{this.TranslationId}";

        public Translator()
        {
        }

        protected abstract bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason);

        protected abstract void Translate(
            SubtitleGeneratorContext context,
            Translation translation);

        protected Transcription GetTranscription(
            SubtitleGeneratorContext context)
        {
            return context.WIP.Transcriptions.FirstOrDefault(f => f.Id == TranscriptionId && f.IsFinished);
        }

        protected abstract string GetMetadataProduced();

        public override void Execute(
            SubtitleGeneratorContext context)
        {
            if (!this.Enabled)
            {
                return;
            }

            var translation = context.WIP.Translations.FirstOrDefault(
                t => t.TranscriptionId == this.TranscriptionId && t.TranslationId == this.TranslationId);

            if (translation?.IsFinished == true)
            {
                context.WriteInfoAlreadyDone($"Translation '{this.FullId}' have already been done.");
                context.WriteInfoAlreadyDone();
            }
            else if (!this.IsPrerequisitesMet(context, out var reason))
            {
                context.WriteInfoAlreadyDone($"Translation '{this.FullId}' cannot start yet because: {reason}");
                context.WriteInfoAlreadyDone();
            }
            else
            {
                if (translation == null)
                {
                    translation = new Translation(
                        this.TranscriptionId,
                        this.TranslationId,
                        this.GetMetadataProduced(),
                        this.TargetLanguage);
                    context.WIP.Translations.Add(translation);
                }

                try
                {
                    var watch = Stopwatch.StartNew();
                    context.WriteInfo($"Translating '{this.FullId}'...");
                    this.Translate(
                        context,
                        translation);

                    context.WriteInfo($"Finished in {watch.Elapsed}.");
                    context.WriteInfo();
                }
                catch (Exception ex)
                {
                    context.WriteError($"An error occured while translating '{this.FullId}':\n{ex.Message}");
                    context.WriteLog(ex.ToString());
                }
            }
        }
    }
}