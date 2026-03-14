using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class MergeRuleDictionary : Dictionary<string, string>
    {
        public MergeRuleDictionary()
            : base(StringComparer.OrdinalIgnoreCase)
        {

        }

        public string GetFinalKey(string sourceId, string oldKey)
        {
            return this.TryGetValue(oldKey, out var newGenericKey)
                ? newGenericKey
                : this.TryGetValue($"{sourceId},{oldKey}", out var newSpecificKey)
                ? newSpecificKey
                : oldKey;
        }
    }
}