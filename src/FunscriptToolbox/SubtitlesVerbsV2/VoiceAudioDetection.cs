using System;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    public class VoiceAudioDetection
    {
        public static VoiceAudioDetection FromText(TimeSpan startTime, TimeSpan endTime, string text)
        {
            var match = Regex.Match(text, @"^\s*(Context:(?<textContext>.*)|Screengrab:(?<textScreengrab>.*))", RegexOptions.IgnoreCase);
            if (match.Groups["textContext"].Success)
            {
                return new VoiceAudioDetection(startTime, endTime, VoiceAudioDectectionType.Context, match.Groups["textContext"].Value.Trim());
            }
            else if (match.Groups["textScreengrab"].Success)
            {
                return new VoiceAudioDetection(startTime, endTime, VoiceAudioDectectionType.Screengrab, match.Groups["textScreengrab"].Value.Trim());
            }
            else
            {
                return new VoiceAudioDetection(startTime, endTime, VoiceAudioDectectionType.Voice, text);
            }
        }

        public VoiceAudioDectectionType Type { get; }
        public TimeSpan Start { get; }
        public TimeSpan End { get; }
        public TimeSpan Duration => End - Start;
        public string Text { get; }

        public VoiceAudioDetection(TimeSpan start, TimeSpan end, VoiceAudioDectectionType type, string text)
        {
            Start = start;
            End = end;
            Type = type;
            Text = text;
        }
    }
}