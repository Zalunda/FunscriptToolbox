using System;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    public class SubtitleForcedTiming
    {
        public static SubtitleForcedTiming FromText(
            TimeSpan startTime, 
            TimeSpan endTime, 
            string text)
        {
            var match = Regex.Match(text, @"^\s*(Context:(?<textContext>.*)|Screengrab:(?<textScreengrab>.*))", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Groups["textContext"].Success)
            {
                return new SubtitleForcedTiming(startTime, endTime, SubtitleForcedTimingType.Context, match.Groups["textContext"].Value.Trim());
            }
            else if (match.Groups["textScreengrab"].Success)
            {
                return new SubtitleForcedTiming(startTime, endTime, SubtitleForcedTimingType.Screengrab, match.Groups["textScreengrab"].Value.Trim());
            }
            else
            {
                return new SubtitleForcedTiming(startTime, endTime, SubtitleForcedTimingType.Voice, text);
            }
        }

        public SubtitleForcedTimingType Type { get; }
        public TimeSpan StartTime { get; }
        public TimeSpan EndTime { get; }
        public TimeSpan Duration => EndTime - StartTime;
        public string Text { get; }

        public SubtitleForcedTiming(
            TimeSpan startTime, 
            TimeSpan endTime, 
            SubtitleForcedTimingType type, 
            string text)
        {
            StartTime = startTime;
            EndTime = endTime;
            Type = type;
            Text = text;
        }
    }
}