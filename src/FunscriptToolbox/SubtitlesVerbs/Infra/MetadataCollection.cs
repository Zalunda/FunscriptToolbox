using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class MetadataCollection : Dictionary<string, string>
    {
        public static MetadataCollection CreateSimple(string key, string name)
        {
            return new MetadataCollection() {{key, name}};
        }

        [JsonConstructor]
        public MetadataCollection()
            : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        public MetadataCollection(Dictionary<string, string> items)
            : base(items, StringComparer.OrdinalIgnoreCase)
        {
        }

        // --- Generic Accessors ---
        public string Get(string key) { this.TryGetValue(key, out var v); return v; }

        public void Merge(MetadataCollection other, Dictionary<string, string> mergeRules = null)
        {
            foreach (var kvp in other)
            {
                string renamedKey = null;
                var foundRule = mergeRules?.TryGetValue(kvp.Key, out renamedKey);
                if (foundRule == true)
                {
                    if (renamedKey != null)
                    {
                        this[renamedKey] = kvp.Value;
                    }
                    else
                    {
                        // Ignore this metadata
                    }
                }
                else
                {
                    this[kvp.Key] = kvp.Value;
                }
            }
        }
    }
}