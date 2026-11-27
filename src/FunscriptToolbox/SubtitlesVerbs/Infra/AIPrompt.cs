using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    [JsonObject]
    public class AIPrompt
    {
        public const string TranscriptionLanguageToken = "[TranscriptionLanguage]";
        public const string TranslationLanguageToken = "[TranslationLanguage]";

        [ConfigDoc("Text added before Text (ex. in an override file, an instruction added to a prompt of the main configuration without repeating it).")]
        [JsonProperty(Order = 1)]
        public string TextBefore { get; }

        [ConfigDoc("The text of the prompt. [TranscriptionLanguage] / [TranslationLanguage] are replaced by the languages (ex. \"Japanese\", \"English\").\n" +
            "In the files, a long text can be written on several lines between '=___...' and '___=' (no '\\n' needed).")]
        [JsonProperty(Order = 2)]
        public string Text { get; }

        [ConfigDoc("Text added after Text (ex. in an override file, your preferences added to a prompt of the main configuration, like the tones of the translation).",
            Advice = "The best place for your own instructions: the prompt itself can be replaced by a new version of the main configuration, TextAfter in your .override.user.config stays.")]
        [JsonProperty(Order = 3)]
        public string TextAfter { get; }

        [ConfigDoc("The prompt as a list of lines (used when Text is not set).")]
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