using FunscriptToolbox.Core;
using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbsV2
{
    public class SubtitleForcedTimingParser
    {
        public string FileSuffix { get; set; } = ".perfect-vad.srt";

        public string PatternNoVoice { get; set; } = "{NoVoice}";
        public string PatternContext { get; set; } = "{Context:(?<text>[^}]*)}";
        public string PatternScreengrab { get; set; } = "{Screengrab:(?<text>[^}]*)}";
        public string PatternTalker { get; set; } = "{Talker:(?<text>[^}]*)}";
        public string PatternAudioNormalization { get; set; } = "{NormAudio(:)?(?<text>[^}]*)}";

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
                        var audioNormalization = GetMatchedText(subtitle.Text, this.PatternAudioNormalization);
                        return new SubtitleForcedTiming(
                                subtitle.StartTime,
                                subtitle.EndTime,
                                noVoice || screengrabText != null ? null : subtitle.Text,
                                talker,
                                contextText, 
                                screengrabText,
                                audioNormalization);
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