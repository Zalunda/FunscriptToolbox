using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.Core.Infra
{
    public static class TimeSpanExtensions
    {
        public static TimeSpan Sum<TSource>(this IEnumerable<TSource> source, Func<TSource, TimeSpan> selector)
        {
            return source.Select(selector).Aggregate(TimeSpan.Zero, (acc, ts) => acc + ts);
        }
    }
}
