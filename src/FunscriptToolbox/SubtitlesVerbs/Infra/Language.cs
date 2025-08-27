using Newtonsoft.Json;
using System;
using System.Globalization;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    [JsonConverter(typeof(LanguageConverter))]
    public class Language
    {
        public static Language FromString(string language)
        {
            foreach (var culture in CultureInfo.GetCultures(CultureTypes.AllCultures))
            {
                if (string.Equals(language, culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(language, culture.DisplayName, StringComparison.OrdinalIgnoreCase))
                    return new Language(culture.TwoLetterISOLanguageName, culture.DisplayName);
            }
            return null;
        }

        public string ShortName { get; }
        public string LongName { get; }

        public Language(string shortName, string longName)
        {
            ShortName = shortName;
            LongName = longName;
        }
    }
}