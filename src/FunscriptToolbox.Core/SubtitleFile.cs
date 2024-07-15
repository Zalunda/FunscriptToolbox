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
            try
            {
                return new SubtitleFile(
                    filepath,
                    ReadSrtSubtitles(
                        File.ReadLines(filepath).ToArray()));
            }
            catch (Exception ex)
            {
                throw new Exception($"Cannot parse srt file '{filepath}': {ex.Message}", ex);
            }
        }

        private static IEnumerable<Subtitle> ReadSrtSubtitles(string[] lines)
        {
            var frCulture = CultureInfo.GetCultureInfo("fr-FR"); // fr culture will accept "," for the milliseconds separator

            int currentSubtitleNumber = 0;
            TimeSpan currentStartTime = TimeSpan.Zero;
            TimeSpan currentEndTime = TimeSpan.Zero;
            var texts = new List<string>();

            var index = 0;
            while (index < lines.Length)
            {
                var currentLine = lines[index];
                var subtitleTimesLine = (index + 1 < lines.Length) ? lines[index + 1].Trim() : string.Empty;
                var matchTimes = rs_timesRegex.Match(subtitleTimesLine);
                if (int.TryParse(currentLine.Trim(), out var subtitleNumber) && matchTimes.Success)
                {
                    if (texts.Count > 0)
                    {
                        yield return new Subtitle(
                            currentStartTime,
                            currentEndTime,
                            texts.Where(line => line.Length > 0).ToArray(),
                            currentSubtitleNumber);
                    }

                    currentSubtitleNumber = subtitleNumber;
                    currentStartTime = TimeSpan.Parse(matchTimes.Groups["StartTime"].Value, frCulture);
                    currentEndTime = TimeSpan.Parse(matchTimes.Groups["EndTime"].Value, frCulture);
                    texts.Clear();
                    index += 2;
                }
                else
                {
                    texts.Add(currentLine);
                    index++;
                }
            }

            if (texts.Count > 0)
            {
                yield return new Subtitle(
                    currentStartTime,
                    currentEndTime,
                    texts.Where(line => line.Length > 0).ToArray(),
                    currentSubtitleNumber);
            }
        }

        public string FilePath { get; }
        public List<Subtitle> Subtitles { get; }

        public SubtitleFile(string filepath = null, IEnumerable<Subtitle> subtitles = null)
        {
            this.FilePath = filepath;
            this.Subtitles = subtitles == null ? new List<Subtitle>() : subtitles.ToList();
        }

        public void ExpandTiming(TimeSpan minDuration, TimeSpan durationAdded)
        {
            var oldSubtitles = this.Subtitles
                .OrderBy(f => f.StartTime)
                .ThenBy(f => f.EndTime)
                .ToArray();
            this.Subtitles.Clear();
            for (int i = 0; i < oldSubtitles.Length; i++)
            {
                var subtitle = oldSubtitles[i];
                var nextSubtitle = (i + 1 < oldSubtitles.Length) ? oldSubtitles[i + 1] : null;

                var newDuration = subtitle.Duration + durationAdded;
                if (newDuration < minDuration)
                {
                    newDuration = minDuration;
                }

                var newEndTime = subtitle.StartTime + newDuration;
                if (newEndTime > nextSubtitle?.StartTime)
                {
                    newEndTime = nextSubtitle.StartTime;
                }

                this.Subtitles.Add(
                    new Subtitle(
                        subtitle.StartTime, 
                        newEndTime, 
                        subtitle.Lines));
            }
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
