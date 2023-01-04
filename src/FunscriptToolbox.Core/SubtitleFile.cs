using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.Core
{
    public class SubtitleFile
    {
        public const string SrtExtension = ".srt";
        private readonly static Regex rs_timesRegex = new Regex(@"^\s*(?<StartTime>\d+:\d+:\d+(,\d+)?)\s*-->\s*(?<EndTime>\d+:\d+:\d+(,\d+)?)\s*$", RegexOptions.Compiled);

        public static SubtitleFile FromSrtFile(string filepath)
        {
            return new SubtitleFile(
                filepath,
                ReadSrtSubtitles(
                    File.ReadLines(filepath).ToArray()));
        }

        private static IEnumerable<Subtitle> ReadSrtSubtitles(string[] lines)
        {
            var index = 0;
            while (index < lines.Length)
            {
                var number = int.Parse(lines[index++]);
                var times = lines[index++];
                var match = rs_timesRegex.Match(times);
                if (!match.Success)
                {
                    throw new FormatException($"{index}: Invalid format for start/end time: {times}");
                }

                var texts = new List<string>();
                while (index < lines.Length && !string.IsNullOrEmpty(lines[index]))
                {
                    texts.Add(lines[index++]);
                }
                while (index < lines.Length && string.IsNullOrEmpty(lines[index]))
                {
                    index++; // skip empty line
                }

                var frCulture = CultureInfo.GetCultureInfo("fr-FR"); // fr culture will accept "," for the milliseconds separator
                yield return new Subtitle(
                    TimeSpan.Parse(match.Groups["StartTime"].Value, frCulture), 
                    TimeSpan.Parse(match.Groups["EndTime"].Value, frCulture), 
                    texts.ToArray(),
                    number);
            }
        }

        public string FilePath { get; }
        public List<Subtitle> Subtitles { get; }

        public SubtitleFile(string filepath = null, IEnumerable<Subtitle> subtitles = null)
        {
            this.FilePath = filepath;
            this.Subtitles = subtitles == null ? new List<Subtitle>() : subtitles.ToList();
        }

        public void SaveSrt(string filepath = null)
        {
            File.WriteAllLines(
                filepath ?? this.FilePath ?? throw new ArgumentNullException(nameof(filepath)),
                GetSrtLines(this.Subtitles),
                Encoding.UTF8);
        }

        private IEnumerable<string> GetSrtLines(List<Subtitle> subtitles)
        {
            int index = 1; 
            foreach (var subtitle in subtitles
                .OrderBy(f => f.StartTime)
                .ThenBy(f => f.EndTime))
            {
                yield return index++.ToString();
                yield return $"{FormatTimespan(subtitle.StartTime)} --> {FormatTimespan(subtitle.EndTime)}";
                foreach (var line in subtitle.Lines)
                {
                    yield return line;
                }
                yield return string.Empty;
            }
        }

        private string FormatTimespan(TimeSpan time)
        {
            return $"{time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2},{time.Milliseconds:D3}";
        }
    }
}
