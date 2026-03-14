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

        public void Merge(
            MetadataCollection other, 
            MergeRuleDictionary mergeRules = null, 
            ValueTransformationRuleDictionary valueTransformationRules = null, 
            string sourceId = null, 
            string[] sourcePrivateMetadataNames = null)
        {
            foreach (var kvp in other)
            {
                if (sourcePrivateMetadataNames?.Contains(kvp.Key) == true)
                {
                    // Ignore this metadata
                }
                else
                {
                    var newKey = mergeRules == null
                        ? kvp.Key
                        : mergeRules.GetFinalKey(sourceId, kvp.Key);
                    var newValue = valueTransformationRules == null
                        ? kvp.Value
                        : valueTransformationRules.GetFinalValue(sourceId, kvp.Key, kvp.Value);
                    if (newKey != null)
                    {
                        this[newKey] = newValue;
                    }
                }
            }
        }
    }
}