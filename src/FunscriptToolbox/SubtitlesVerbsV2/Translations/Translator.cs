using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using FunscriptToolbox.SubtitlesVerbV2;
using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    public abstract class Translator
    {
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        [JsonProperty(Order = 2)]
        public string TranslationId { get; set; }

        [JsonProperty(Order = 3)]
        public Language TargetLanguage { get; set; } = Language.FromString("en");

        public Translator()
        {
        }

        public abstract void Translate(
            SubtitleGeneratorContext context,
            string baseFilePath,
            Transcription transcription,
            Translation translation);
    }

}