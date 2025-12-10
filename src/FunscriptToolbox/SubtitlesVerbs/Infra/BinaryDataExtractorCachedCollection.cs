using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class BinaryDataExtractorCachedCollection
    {
        private Dictionary<TimeSpan, Dictionary<string, AIRequestPart[]>> r_cache;
        private readonly Dictionary<string, BinaryDataExtractorExtended> r_extractors;

        public BinaryDataExtractorCachedCollection(params BinaryDataExtractorExtended[] extractors)
        {
            r_cache = new Dictionary<TimeSpan, Dictionary<string, AIRequestPart[]>>();
            r_extractors = extractors
                .Where(extractor => extractor.Extractor.Enabled)
                .ToDictionary(item => item.Extractor.OutputFieldName, item => item);
        }

        internal BinaryDataType? GetDefaultDataType()
        {
            return r_extractors.Count == 1
                ? r_extractors.First().Value.Extractor.DataType
                : (BinaryDataType?) null;
        }

        public Dictionary<string, AIRequestPart[]> GetNamedContentListForTiming(
            AIRequestSection section,
            ITiming timing, 
            string text = null)
        {
            if (!r_cache.TryGetValue(timing.StartTime, out var data))
            {
                data = r_extractors
                    .Select(kvp => new { field = kvp.Key, data = kvp.Value.GetData(section, timing, text) })
                    .ToDictionary(item => item.field, item => item.data);
                r_cache[timing.StartTime] = data;
            }
            return data;
        }

        public Dictionary<string, AIRequestPart[]> GetNamedContentListForItem(
            AIRequestSection section,
            TimedItemWithMetadata item, 
            string text = null)
        {
            if (!r_cache.TryGetValue(item.StartTime, out var data))
            {
                data = r_extractors
                    .Where(extractor => extractor.Value.Extractor.MetadataForSkipping == null || !item.Metadata.ContainsKey(extractor.Value.Extractor.MetadataForSkipping))
                    .Select(kvp => new { field = kvp.Key, data = kvp.Value.GetData(section, item, text) })
                    .ToDictionary(item => item.field, item => item.data);
                r_cache[item.StartTime] = data;
            }
            return data;
        }

        public IEnumerable<(TimeSpan time, (string name, BinaryDataType dataType, AIRequestPart[] contentList)[] binaryItems)> GetContextOnlyNodes(
            AIRequestSection section,
            ITiming timing, 
            MetadataCollection metadatas,
            Func<TimeSpan, string> getText)
        {
            return r_extractors
                .Where(extractor => extractor.Value.Extractor.MetadataForSkipping == null || !metadatas.ContainsKey(extractor.Value.Extractor.MetadataForSkipping))
                .SelectMany(bde => bde.Value.GetContextOnlyNodes(section, timing, getText))
                .GroupBy(item => item.time)
                .ToDictionary(item => item.Key, item => item.ToArray())
                .Select(kvp => (kvp.Key, kvp.Value.Select(x => (x.name, x.dataType, x.contentList)).ToArray()));
        }

        internal IEnumerable<AIRequestPart> GetTrainingContentList()
        {
            return r_extractors.SelectMany(x => x.Value.TrainingContentLists);
        }
    }
}