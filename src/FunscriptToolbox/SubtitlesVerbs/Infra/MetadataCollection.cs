using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class MetadataCollection : Dictionary<string, string>
    {
        public static MetadataCollection CreateSimple(string key, string value)
        {
            return new MetadataCollection() {{key, value}};
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

        public void Merge(MetadataCollection other, Dictionary<string, string> mergeRules = null, string id = null, string[] privateMetadataNames = null)
        {
            foreach (var kvp in other)
            {
                string renamedKeyGeneric = null;
                var foundRuleGeneric = mergeRules?.TryGetValue(kvp.Key, out renamedKeyGeneric);
                string renamedKeySpecific = null;
                var foundRuleSpecific = mergeRules?.TryGetValue($"{id},{kvp.Key}", out renamedKeySpecific);
                if (privateMetadataNames?.Contains(kvp.Key) == true)
                {
                    // Ignore this metadata
                }
                else if (foundRuleGeneric == true)
                {
                    if (renamedKeyGeneric != null)
                    {
                        this[renamedKeyGeneric] = kvp.Value;
                    }
                    else
                    {
                        // Ignore this metadata
                    }
                }
                else if (foundRuleSpecific == true)
                {
                    if (renamedKeySpecific != null)
                    {
                        this[renamedKeySpecific] = kvp.Value;
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