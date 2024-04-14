using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public class LanguageConverter : JsonConverter<Language>
    {
        public override Language ReadJson(JsonReader reader, Type objectType, Language existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            return Language.FromString((string)reader.Value);
        }

        public override void WriteJson(JsonWriter writer, Language value, JsonSerializer serializer)
        {
            writer.WriteValue(((Language)value).LongName);
        }
    }
}