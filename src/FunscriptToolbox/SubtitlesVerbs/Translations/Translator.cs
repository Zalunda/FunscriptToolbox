using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using Newtonsoft.Json;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
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

        public virtual bool IsPrerequisitesMet(
            Transcription transcription, 
            out string reason)
        {
            reason = null;
            return true;
        }

        public bool IsFinished(
            Transcription transcription,
            Translation translation)
        {
            return translation.IsFinished(transcription);
        }

        public abstract void Translate(
            SubtitleGeneratorContext context,
            Transcription transcription,
            Translation translation);
    }
}