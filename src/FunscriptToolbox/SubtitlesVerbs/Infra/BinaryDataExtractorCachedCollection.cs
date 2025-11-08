using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class BinaryDataExtractorCachedCollection
    {
        private Dictionary<TimeSpan, Dictionary<string, dynamic[]>> r_cache;
        private readonly Dictionary<string, BinaryDataExtractorExtended> r_extractors;

        public BinaryDataExtractorCachedCollection(params BinaryDataExtractorExtended[] extractors)
        {
            r_cache = new Dictionary<TimeSpan, Dictionary<string, dynamic[]>>();
            r_extractors = extractors.ToDictionary(item => item.OutputFieldName, item => item);
        }

        public Dictionary<string, dynamic[]> GetNamedContentListForTiming(ITiming timing, string text = null)
        {
            if (!r_cache.TryGetValue(timing.StartTime, out var data))
            {
                data = r_extractors
                    .Select(kvp => new { field = kvp.Key, data = kvp.Value.GetData(timing, text) })
                    .ToDictionary(item => item.field, item => item.data);
                r_cache[timing.StartTime] = data;
            }
            return data;
        }

        internal IEnumerable<dynamic> GetTrainingContentList()
        {
            return r_extractors.SelectMany(x => x.Value.TrainingContentLists);
        }
    }
}