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

        /// <summary>
        /// The name of a metadata after the rules: "&lt;metadata&gt;", then "&lt;source&gt;,&lt;metadata&gt;", then "&lt;source&gt;,*" (every
        /// metadata of the source), then "*" (every metadata); none: unchanged. null: the metadata is removed.
        /// </summary>
        public string GetFinalKey(string sourceId, string oldKey)
        {
            return this.TryGetValue(oldKey, out var newGenericKey)
                ? newGenericKey
                : this.TryGetValue($"{sourceId},{oldKey}", out var newSpecificKey)
                ? newSpecificKey
                : this.TryGetValue($"{sourceId},*", out var newSourceKey)
                ? newSourceKey
                : this.TryGetValue("*", out var newAnyKey)
                ? newAnyKey
                : oldKey;
        }
    }
}