using System.Text.RegularExpressions;
using System;
using FunscriptToolbox.Core;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    public class SubtitleForcedTimingParser
    {
        public string PatternNoVoice { get; set; } = "{NoVoice}";
        public string PatternContext { get; set; } = "{Context:(?<text>[^}]*)}";
        public string PatternScreengrab { get; set; } = "{Screengrab:(?<text>[^}]*)}";
        public string PatternTalker { get; set; } = "{Talker:(?<text>[^}]*)}";

        public SubtitleForcedTimingCollection ParseFromFile(string filepath)
        {
            return new SubtitleForcedTimingCollection(
                SubtitleFile
                .FromSrtFile(filepath)
                .Subtitles
                .Select(
                    subtitle => {
                        var noVoice = Regex.IsMatch(subtitle.Text, this.PatternNoVoice, RegexOptions.IgnoreCase);
                        var contextText = GetMatchedText(subtitle.Text, this.PatternContext);
                        var screengrabText = GetMatchedText(subtitle.Text, this.PatternScreengrab);
                        var talker = GetMatchedText(subtitle.Text, this.PatternTalker);
                        return new SubtitleForcedTiming(
                                subtitle.StartTime,
                                subtitle.EndTime,
                                noVoice || screengrabText != null ? null : subtitle.Text,
                                talker,
                                contextText);
                    }));
        }

        public static string GetMatchedText(string text, string pattern)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success)
                return null;

            if (match.Groups["text"].Success)
                return match.Groups["text"].Value;
            else
                throw new Exception($"Pattern '{pattern}' doesn't return a group named 'text'.");
        }
    }
}