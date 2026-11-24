using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    /// <summary>
    /// Reads the job state of a worker saved in a .wipsubtitles ('$type' + its content), and reads it as null when it can't be read anymore:
    /// its class doesn't exist anymore ('$type' of an older version: renamed, removed), or its content doesn't fit the class anymore (changed
    /// since). The worker then starts its job again, as if it had none; the rest of the file is read as usual.
    /// Written as before (CanWrite = false: the '$type' of the property, see TypeNameHandling.Auto on it).
    /// </summary>
    public class TolerantJobStateConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => true;
        public override bool CanWrite => false;

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) => throw new NotSupportedException();

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var token = JToken.Load(reader);
            if (token.Type != JTokenType.Object)
                return null;

            var jobState = (JObject)token;
            var typeName = (string)jobState["$type"];
            var type = ResolveType(serializer, typeName);
            if (type == null || !objectType.IsAssignableFrom(type))
                return null;

            // (the '$type' is already used: read with the same serializer, its '$id' kept)
            jobState.Remove("$type");
            try
            {
                return jobState.ToObject(type, serializer);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>The class of '$type' ("Namespace.Class+Nested, Assembly"), or null when it doesn't exist anymore.</summary>
        private static Type ResolveType(JsonSerializer serializer, string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;

            // (the assembly: after the first ',' outside of '[...]', ex. the arguments of a generic type)
            int depth = 0, comma = -1;
            for (int i = 0; i < typeName.Length && comma < 0; i++)
            {
                var c = typeName[i];
                if (c == '[') depth++;
                else if (c == ']') depth--;
                else if (c == ',' && depth == 0) comma = i;
            }
            var name = (comma < 0 ? typeName : typeName.Substring(0, comma)).Trim();
            var assemblyName = comma < 0 ? null : typeName.Substring(comma + 1).Trim();
            try
            {
                return serializer.SerializationBinder.BindToType(assemblyName, name);
            }
            catch (JsonSerializationException)
            {
                return null;
            }
        }
    }
}
