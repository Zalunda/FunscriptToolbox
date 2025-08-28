using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class CachedBinaryGenerator
    {
        private Dictionary<TimeSpan, dynamic[]> r_cache;
        private readonly Func<ITiming, dynamic[]> r_createBinaryFunc;

        public CachedBinaryGenerator(Func<ITiming, dynamic[]> createBinaryFunc)
        {
            r_cache = new Dictionary<TimeSpan, dynamic[]>();
            r_createBinaryFunc = createBinaryFunc;
        }

        public dynamic[] GetBinaryContent(ITiming timing)
        {
            if (!r_cache.TryGetValue(timing.StartTime, out var data))
            {
                data = r_createBinaryFunc(timing);
                r_cache[timing.StartTime] = data;
            }
            return data;
        }
    }
}