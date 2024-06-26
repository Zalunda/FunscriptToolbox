﻿using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    [JsonObject]
    public class AIPrompt
    {
        public const string TranscriptionLanguageToken = "[TranscriptionLanguage]";
        public const string TranslationLanguageToken = "[TranslationLanguage]";

        [JsonProperty(Required = Required.Always)]
        public string[] Lines { get; }

        public AIPrompt(IEnumerable<string> lines)
        {
            this.Lines = lines.ToArray();
        }

        public string GetFinalText(
            Language transcribedLanguage,
            Language translatedLanguage)
        {
            return string.Join("\n", this.Lines)
                .Replace(TranscriptionLanguageToken, transcribedLanguage.LongName)
                .Replace(TranslationLanguageToken, translatedLanguage.LongName);
        }
    }
}