using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;

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
                return serializer.SerializationBinder.BindToType(assemblyName, name) ?? ResolveByClassName(name);
            }
            catch (JsonSerializationException)
            {
                return ResolveByClassName(name);
            }
        }

        /// <summary>
        /// The steps renamed since their job state was saved (old class name => new class name): their job state is still read.
        /// (3.3: the transcribers / translators that work on tracks became transforms)
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> rs_renamedClasses = new System.Collections.Generic.Dictionary<string, string>
        {
            { "TranscriberAIManualInputPreparer", "TransformManualInputPreparer" },
            { "TranscriberInteractifReview", "TransformReview" },
            { "TranscriberClone", "TransformClone" },
            { "TranscriberAI", "TransformAI" },
        };

        /// <summary>
        /// The class of a job state when its full name doesn't exist anymore: found by its class name only ("Class+JobState"), in any
        /// namespace (the classes can be moved), after the renames of rs_renamedClasses. Null: not found (or more than one).
        /// </summary>
        private static Type ResolveByClassName(string fullName)
        {
            var plus = fullName.IndexOf('+');
            var outerFull = plus < 0 ? fullName : fullName.Substring(0, plus);
            var nested = plus < 0 ? null : fullName.Substring(plus + 1);
            var outer = outerFull.Substring(outerFull.LastIndexOf('.') + 1);
            if (rs_renamedClasses.TryGetValue(outer, out var renamed))
                outer = renamed;

            var candidates = typeof(TolerantJobStateConverter).Assembly.GetTypes()
                .Where(t => nested == null
                    ? t.Name == outer && t.DeclaringType == null
                    : t.Name == nested && t.DeclaringType?.Name == outer)
                .ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
    }
}
