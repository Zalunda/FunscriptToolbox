using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class BinaryDataExtractorCachedCollection
    {
        private readonly Dictionary<(TimeSpan, TimeSpan), Dictionary<string, AIRequestPart[]>> r_cache;
        private readonly Dictionary<(TimeSpan, TimeSpan), (TimeSpan time, (string name, BinaryDataType dataType, AIRequestPart[] contentList)[] binaryItems)[]> r_contextNodesCache;
        protected readonly Dictionary<string, BinaryDataExtractorExtended> r_extractors;

        /// <summary>Called (when not silent) each time GetNamedContentListForItem is served from the in-memory cache.</summary>
        public Action<ITiming> OnCacheHit { get; set; }

        public BinaryDataExtractorCachedCollection(params BinaryDataExtractorExtended[] extractors)
        {
            r_cache = new Dictionary<(TimeSpan, TimeSpan), Dictionary<string, AIRequestPart[]>>();
            r_contextNodesCache = new Dictionary<(TimeSpan, TimeSpan), (TimeSpan time, (string name, BinaryDataType dataType, AIRequestPart[] contentList)[] binaryItems)[]>();
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
            string text = null,
            bool silent = false)
        {
            var cacheKey = (timing.StartTime, timing.EndTime);
            lock (r_cache)
            {
                if (r_cache.TryGetValue(cacheKey, out var cachedData))
                {
                    return cachedData;
                }
            }

            var data = r_extractors
                .Select(kvp => new { field = kvp.Key, data = kvp.Value.GetData(section, timing, text, silent) })
                .ToDictionary(item => item.field, item => item.data);

            lock (r_cache)
            {
                if (!r_cache.ContainsKey(cacheKey))
                {
                    r_cache[cacheKey] = data;
                }
                return r_cache[cacheKey];
            }
        }

        public virtual Dictionary<string, AIRequestPart[]> GetNamedContentListForItem(
            AIRequestSection section,
            TimedItemWithMetadata item, 
            string text = null,
            bool silent = false)
        {
            var cacheKey = (item.StartTime, item.EndTime);
            lock (r_cache)
            {
                if (r_cache.TryGetValue(cacheKey, out var cachedData))
                {
                    if (!silent)
                    {
                        this.OnCacheHit?.Invoke(item);
                    }
                    return cachedData;
                }
            }

            var data = r_extractors
                .Where(extractor => extractor.Value.Extractor.MetadataForSkipping == null || !item.Metadata.ContainsKey(extractor.Value.Extractor.MetadataForSkipping))
                .Select(kvp => new { field = kvp.Key, data = kvp.Value.GetData(section, item, text, silent) })
                .ToDictionary(item => item.field, item => item.data);

            lock (r_cache)
            {
                if (!r_cache.ContainsKey(cacheKey))
                {
                    r_cache[cacheKey] = data;
                }
                return r_cache[cacheKey];
            }
        }

        public IEnumerable<(TimeSpan time, (string name, BinaryDataType dataType, AIRequestPart[] contentList)[] binaryItems)> GetContextOnlyNodes(
            AIRequestSection section,
            ITiming timing, 
            MetadataCollection metadatas,
            Func<TimeSpan, string> getText,
            bool silent = false)
        {
            var cacheKey = (timing.StartTime, timing.EndTime);
            lock (r_contextNodesCache)
            {
                if (r_contextNodesCache.TryGetValue(cacheKey, out var cachedData))
                {
                    if (!silent && cachedData.Length > 0)
                    {
                        this.OnCacheHit?.Invoke(timing);
                    }
                    return cachedData;
                }
            }

            var data = r_extractors
                .Where(extractor => extractor.Value.Extractor.MetadataForSkipping == null || !metadatas.ContainsKey(extractor.Value.Extractor.MetadataForSkipping))
                .SelectMany(bde => bde.Value.GetContextOnlyNodes(section, timing, getText, silent))
                .GroupBy(item => item.time)
                .ToDictionary(item => item.Key, item => item.ToArray())
                .Select(kvp => (kvp.Key, kvp.Value.Select(x => (x.name, x.dataType, x.contentList)).ToArray()))
                .ToArray();

            lock (r_contextNodesCache)
            {
                if (!r_contextNodesCache.ContainsKey(cacheKey))
                {
                    r_contextNodesCache[cacheKey] = data;
                }
                return r_contextNodesCache[cacheKey];
            }
        }

        internal IEnumerable<AIRequestPart> GetTrainingContentList()
        {
            return r_extractors.SelectMany(x => x.Value.TrainingContentLists);
        }
    }
}