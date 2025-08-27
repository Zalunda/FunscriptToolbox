using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using Newtonsoft.Json;

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

        public Translator()
        {
        }

        public abstract bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            Transcription transcription,
            out string reason);

        public abstract void Translate(
            SubtitleGeneratorContext context,
            Transcription transcription,
            Translation translation);
    }
}