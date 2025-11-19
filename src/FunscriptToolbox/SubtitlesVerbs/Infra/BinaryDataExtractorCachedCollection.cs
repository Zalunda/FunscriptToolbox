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
            r_extractors = extractors
                .Where(extractor => extractor.Extractor.Enabled)
                .ToDictionary(item => item.Extractor.OutputFieldName, item => item);
        }

        public Dictionary<string, dynamic[]> GetNamedContentListForTiming(
            ITiming timing, 
            string text = null)
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

        public Dictionary<string, dynamic[]> GetNamedContentListForItem(
            TimedItemWithMetadata item, 
            string text = null)
        {
            if (!r_cache.TryGetValue(item.StartTime, out var data))
            {
                data = r_extractors
                    .Where(extractor => extractor.Value.Extractor.MetadataForSkipping == null || !item.Metadata.ContainsKey(extractor.Value.Extractor.MetadataForSkipping))
                    .Select(kvp => new { field = kvp.Key, data = kvp.Value.GetData(item, text) })
                    .ToDictionary(item => item.field, item => item.data);
                r_cache[item.StartTime] = data;
            }
            return data;
        }

        public IEnumerable<(TimeSpan time, (string name, dynamic[] contentList)[] binaryItems)> GetContextOnlyNodes(
            ITiming timing, 
            MetadataCollection metadatas,
            Func<TimeSpan, string> getText)
        {
            return r_extractors
                .Where(extractor => extractor.Value.Extractor.MetadataForSkipping == null || !metadatas.ContainsKey(extractor.Value.Extractor.MetadataForSkipping))
                .SelectMany(bde => bde.Value.GetContextOnlyNodes(timing, getText))
                .GroupBy(item => item.time)
                .ToDictionary(item => item.Key, item => item.ToArray())
                .Select(kvp => (kvp.Key, kvp.Value.Select(x => (x.name, x.contentList)).ToArray()));
        }

        internal IEnumerable<dynamic> GetTrainingContentList()
        {
            return r_extractors.SelectMany(x => x.Value.TrainingContentLists);
        }
    }
}