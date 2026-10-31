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


        /// <summary>
        /// Subtracts the timing of secondFile from firstFile (firstFile - secondFile).
        /// Any remaining timing fragments from firstFile are added to outputFile.
        /// </summary>
        public static SubtitleFile Subtract(
            SubtitleFile firstFile,
            SubtitleFile secondFile,
            TimeSpan? minDuration = null)
        {
            var resultFile = new SubtitleFile();
            var minSpan = minDuration ?? TimeSpan.FromMilliseconds(0);

            // 1. Merge overlapping/adjacent intervals from secondFile into a clean sorted list
            var blockers = new List<(TimeSpan Start, TimeSpan End)>();
            foreach (var sub in secondFile.Subtitles.OrderBy(s => s.StartTime).ThenBy(s => s.EndTime))
            {
                if (sub.EndTime <= sub.StartTime)
                    continue;
                if (blockers.Count > 0 && sub.StartTime <= blockers[blockers.Count - 1].End)
                {
                    var prev = blockers[blockers.Count - 1];
                    blockers[blockers.Count - 1] = (prev.Start, sub.EndTime > prev.End ? sub.EndTime : prev.End);
                }
                else
                {
                    blockers.Add((sub.StartTime, sub.EndTime));
                }
            }

            // 2. For each subtitle in firstFile, subtract every blocker window that intersects it
            int subNumber = 1;
            foreach (var sub in firstFile.Subtitles.OrderBy(s => s.StartTime))
            {
                var pieces = new List<(TimeSpan Start, TimeSpan End)> { (sub.StartTime, sub.EndTime) };
                foreach (var b in blockers)
                {
                    if (b.End <= sub.StartTime) continue;
                    if (b.Start >= sub.EndTime) break;
                    var nextPieces = new List<(TimeSpan Start, TimeSpan End)>();
                    foreach (var (pStart, pEnd) in pieces)
                    {
                        // No overlap
                        if (b.End <= pStart || b.Start >= pEnd)
                        {
                            nextPieces.Add((pStart, pEnd));
                        }
                        else
                        {
                            // Left piece before blocker
                            if (pStart < b.Start)
                                nextPieces.Add((pStart, b.Start));
                            // Right piece after blocker
                            if (pEnd > b.End)
                                nextPieces.Add((b.End, pEnd));
                        }
                    }
                    pieces = nextPieces;
                    if (pieces.Count == 0) break;
                }

                // 3. Add any remaining slices to outputFile
                foreach (var piece in pieces)
                {
                    if (piece.End - piece.Start >= minSpan)
                    {
                        resultFile.Subtitles.Add(new Subtitle(
                            piece.Start,
                            piece.End,
                            sub.Lines, // keeps the original text/trace for reference
                            subNumber++));
                    }
                }
            }
            return resultFile;
        }
    }
}
