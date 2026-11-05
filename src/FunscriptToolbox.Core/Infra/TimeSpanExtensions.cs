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

        /// <summary>
        /// Rounds to the nearest millisecond. .NET Framework did this implicitly in TimeSpan.FromSeconds/FromMilliseconds;
        /// modern .NET keeps full tick precision, which breaks exact TimeSpan comparisons/keys in WIP files.
        /// </summary>
        public static TimeSpan RoundToMs(this TimeSpan value)
        {
            return TimeSpan.FromTicks((long)Math.Round(value.Ticks / (double)TimeSpan.TicksPerMillisecond) * TimeSpan.TicksPerMillisecond);
        }

        /// <summary>
        /// True if both times are within the given tolerance (default 1 ms) of each other.
        /// </summary>
        public static bool IsAlmostEqual(this TimeSpan value, TimeSpan other, TimeSpan? tolerance = null)
        {
            return (value - other).Duration() <= (tolerance ?? TimeSpan.FromMilliseconds(1));
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
