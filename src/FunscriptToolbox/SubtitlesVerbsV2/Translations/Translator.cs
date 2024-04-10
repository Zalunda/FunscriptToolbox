using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using Newtonsoft.Json;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    public abstract class Translator
    {
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        [JsonProperty(Order = 2, Required = Required.Always)]
        public string TranslationId { get; set; }

        [JsonProperty(Order = 3)]
        public Language TargetLanguage { get; set; } = Language.FromString("en");

        public Translator()
        {
        }

        public bool IsFinished(
            Transcription transcription,
            Translation translation)
        {
            return !transcription
                .Items
                .Any(t => t.TranslatedTexts.FirstOrDefault(tt => tt.Id == translation.Id)?.Text == null);
        }

        public abstract void Translate(
            SubtitleGeneratorContext context,
            Transcription transcription,
            Translation translation);
    }
}