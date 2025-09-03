using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    [JsonObject]
    public class AIPrompt
    {
        public const string TranscriptionLanguageToken = "[TranscriptionLanguage]";
        public const string TranslationLanguageToken = "[TranslationLanguage]";

        [JsonProperty(Required = Required.Always)]
        public string Text { get; }

        [JsonConstructor]
        public AIPrompt(string text)
        {
            this.Text = text;
        }

        public AIPrompt(string[] lines) 
        {
            this.Text = string.Join("\n", lines);
        }

        public string GetFinalText(
            Language transcribedLanguage = null,
            Language translatedLanguage = null)
        {
            return this.Text
                .Replace(TranscriptionLanguageToken, transcribedLanguage?.LongName ?? TranscriptionLanguageToken)
                .Replace(TranslationLanguageToken, translatedLanguage?.LongName ?? TranslationLanguageToken);
        }
    }
}