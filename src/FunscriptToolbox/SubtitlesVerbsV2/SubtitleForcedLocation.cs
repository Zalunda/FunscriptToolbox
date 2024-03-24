using System;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    public class SubtitleForcedLocation
    {
        public static SubtitleForcedLocation FromText(
            TimeSpan startTime, 
            TimeSpan endTime, 
            string text)
        {
            var match = Regex.Match(text, @"^\s*(Context:(?<textContext>.*)|Screengrab:(?<textScreengrab>.*))", RegexOptions.IgnoreCase);
            if (match.Groups["textContext"].Success)
            {
                return new SubtitleForcedLocation(startTime, endTime, SubtitleLocationType.Context, match.Groups["textContext"].Value.Trim());
            }
            else if (match.Groups["textScreengrab"].Success)
            {
                return new SubtitleForcedLocation(startTime, endTime, SubtitleLocationType.Screengrab, match.Groups["textScreengrab"].Value.Trim());
            }
            else
            {
                return new SubtitleForcedLocation(startTime, endTime, SubtitleLocationType.Voice, text);
            }
        }

        public SubtitleLocationType Type { get; }
        public TimeSpan StartTime { get; }
        public TimeSpan EndTime { get; }
        public TimeSpan Duration => EndTime - StartTime;
        public string Text { get; }

        public SubtitleForcedLocation(
            TimeSpan startTime, 
            TimeSpan endTime, 
            SubtitleLocationType type, 
            string text)
        {
            StartTime = startTime;
            EndTime = endTime;
            Type = type;
            Text = text;
        }
    }
}