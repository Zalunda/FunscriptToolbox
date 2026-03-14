using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class ValueTransformationRuleDictionary : Dictionary<string, ValueTransformationRule>
    { 
        public ValueTransformationRuleDictionary()
            : base(StringComparer.OrdinalIgnoreCase)
        {

        }

        internal string GetFinalValue(string sourceId, string key, string oldValue)
        {
            return this.TryGetValue(key, out var genericRule)
                ? genericRule.GetFinalValue(oldValue)
                : this.TryGetValue($"{sourceId},{key}", out var specificRule)
                ? specificRule.GetFinalValue(oldValue)
                : oldValue;
        }
    }
}