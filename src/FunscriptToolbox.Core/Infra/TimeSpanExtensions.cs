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
            // Patch for local AI model
            if (text.StartsWith("000"))
            {
                text = text.Substring(1);
            }
            // Defines the expected formats, from most specific to least specific.
            string[] formats = {
                    @"h\:m\:s\.fff",
                    @"h\:m\:s\.ff",
                    @"h\:m\:s\.f",
                    @"h\:m\:s",
                    @"m\:s\.fff",
                    @"m\:s\.ff",
                    @"m\:s\.f",
                    @"m\:s",
                    @"s\.fff",
                    @"s\.ff",
                    @"s\.f",
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
