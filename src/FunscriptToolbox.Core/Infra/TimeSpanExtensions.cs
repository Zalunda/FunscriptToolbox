using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FunscriptToolbox.Core.Infra
{
    public static class TimeSpanExtensions
    {
        public static TimeSpan Sum<TSource>(this IEnumerable<TSource> source, Func<TSource, TimeSpan> selector)
        {
            return source.Select(selector).Aggregate(TimeSpan.Zero, (acc, ts) => acc + ts);
        }

        public static TimeSpan Min(TimeSpan a, TimeSpan b)
        {
            return a < b ? a : b;
        }

        public static TimeSpan Max(TimeSpan a, TimeSpan b)
        {
            return a > b ? a : b;
        }

        public static TimeSpan FlexibleTimeSpanParse(string text)
        {
            // Defines the expected formats, from most specific to least specific.
            string[] formats = {
                    @"h\:m\:s\.fff",
                    @"h\:m\:s",
                    @"m\:s\.fff",
                    @"m\:s",
                    @"s\.fff",
                    @"s"
            };

            if (TimeSpan.TryParseExact(text, formats, CultureInfo.InvariantCulture, out TimeSpan result))
            {
                return result;
            }

            throw new FormatException($"The input string '{text}' was not in a correct format for a TimeSpan.");
        }
    }
}
