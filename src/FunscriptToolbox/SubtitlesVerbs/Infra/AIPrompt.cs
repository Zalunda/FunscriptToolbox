using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    [JsonObject]
    public class AIPrompt
    {
        public const string TranscriptionLanguageToken = "[TranscriptionLanguage]";
        public const string TranslationLanguageToken = "[TranslationLanguage]";

        [JsonProperty(Order = 1)]
        public string TextBefore { get; }

        [JsonProperty(Order = 2)]
        public string Text { get; }

        [JsonProperty(Order = 3)]
        public string TextAfter { get; }

        [JsonProperty(Order = 4)]
        public string[] Lines { get; }

        [JsonConstructor]
        public AIPrompt(string text, string textBefore = null, string textAfter = null, string[] lines = null)
        {
            this.Text = text == null 
                ? string.Join("\n", lines)
                : $"{textBefore}{text}{textAfter}";
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