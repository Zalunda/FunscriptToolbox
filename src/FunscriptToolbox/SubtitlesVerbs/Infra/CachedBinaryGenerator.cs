using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class CachedBinaryGenerator
    {
        private Dictionary<TimeSpan, dynamic[]> r_cache;
        private readonly string r_dataType;
        private readonly Func<ITiming, string, dynamic[]> r_createBinaryFunc;

        public CachedBinaryGenerator(string dataType, Func<ITiming, string, dynamic[]> createBinaryFunc)
        {
            r_cache = new Dictionary<TimeSpan, dynamic[]>();
            r_dataType = dataType;
            r_createBinaryFunc = createBinaryFunc;
        }

        public dynamic[] GetBinaryContent(ITiming timing, string text = null)
        {
            var (data, _) = GetBinaryContentWithType(timing, text);
            return data;
        }

        public (dynamic[] data, string type) GetBinaryContentWithType(ITiming timing, string text = null)
        {
            if (!r_cache.TryGetValue(timing.StartTime, out var data))
            {
                data = r_createBinaryFunc(timing, text);
                r_cache[timing.StartTime] = data;
            }
            return (data, r_dataType);
        }
    }
}