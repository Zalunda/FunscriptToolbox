using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using System.IO;
using System;
using System.Linq;
using FunscriptToolbox.Core.Infra;
using System.Collections.Generic;

public class VirtualSubtitleFile : SubtitleFile
{
    /// <summary>
    /// Loads subtitles from one or more .srt files, merging them into a single virtual timeline.
    /// </summary>
    internal static VirtualSubtitleFile Load(
        TimelineMap timelineMap, 
        string parentPath, 
        string fileSuffix)
    {
        var virtualFile = new VirtualSubtitleFile(timelineMap);
        var nbFilesFound = 0;

        // 1. Iterate through every video part defined in the project.
        foreach (var segment in timelineMap.Segments)
        {
            var fullpath = Path.Combine(parentPath, segment.Filename);
            string srtPath = Path.ChangeExtension(fullpath, fileSuffix);
            var maxEndTimeForThisPart = segment.Offset + segment.Duration;

            if (File.Exists(srtPath))
            {
                nbFilesFound++;

                // 2. Convert local subtitle times back to the merged virtual timeline.
                foreach (var sub in SubtitleFile
                    .FromSrtFile(srtPath)
                    .Subtitles)
                {
                    var startTimeInMerged = segment.Offset + sub.StartTime;
                    var endTimeInMerged = segment.Offset + sub.EndTime;

                    if ((startTimeInMerged > segment.Offset) && (startTimeInMerged < maxEndTimeForThisPart))
                    {
                        virtualFile.Subtitles.Add(new Subtitle(startTimeInMerged, endTimeInMerged, sub.Text));
                    }
                }
            }
        }

        // 3. Ensure the final collection is sorted by time.
        virtualFile.Subtitles.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
        return nbFilesFound > 0 ? virtualFile : null;
    }

    private readonly TimelineMap r_timelineMap;

    // Internal constructor to enforce creation via the factory methods in WIP.
    internal VirtualSubtitleFile(TimelineMap timelineMap)
    {
        r_timelineMap = timelineMap;
    }

    /// <summary>
    /// Saves the subtitles to one or more .srt files, automatically handling splitting for multi-part videos.
    /// </summary>
    public void Save(
        string parentPath, 
        string fileSuffix, 
        Action<string> softDeleteAction = null, 
        SubtitleToInject[] subtitlesToInject = null)
    {
        subtitlesToInject ??= Array.Empty<SubtitleToInject>();

        // Handle subtitles that are NOT injected into every file.
        InjectSubtitleInFile(this, subtitlesToInject.Where(s => !s.InjectInAllFiles), r_timelineMap.Duration);

        var index = 1;
        foreach (var segment in r_timelineMap.Segments)
        {
            var fullpath = Path.Combine(parentPath, segment.Filename);
            var srtFullPath = Path.ChangeExtension(fullpath, fileSuffix);
            var currentSubtitleFile = new SubtitleFile(
                srtFullPath,
                this.Subtitles
                .OrderBy(f => f.StartTime)
                .Where(f => f.StartTime >= segment.Offset && (f.StartTime < segment.Offset + segment.Duration || index == r_timelineMap.Segments.Length)) // We add the subtitle after duration to the last file
                .Select(mergedSub => new Subtitle(
                        mergedSub.StartTime - segment.Offset,
                        mergedSub.EndTime - segment.Offset,
                        mergedSub.Text)));
            index++;

            // Handle subtitles that ARE injected into every file.
            InjectSubtitleInFile(currentSubtitleFile, subtitlesToInject.Where(s => s.InjectInAllFiles), r_timelineMap.Duration);

            softDeleteAction?.Invoke(srtFullPath);
            currentSubtitleFile.SaveSrt();
        }
    }

    public static void InjectSubtitleInFile(SubtitleFile file, IEnumerable<SubtitleToInject> subtitlesToInject, TimeSpan offset)
    {
        var firstSubtitleTime = file.Subtitles.FirstOrDefault()?.StartTime ?? TimeSpan.Zero;
        var lastSubtitleTime = file.Subtitles.LastOrDefault()?.EndTime ?? TimeSpan.MaxValue;
        foreach (var injection in subtitlesToInject)
        {
            if (injection.Origin == SubtitleToInjectOrigin.Start)
            {
                file.Subtitles.Add(new Subtitle(
                    TimeSpanExtensions.Min(firstSubtitleTime, injection.OffsetTime),
                    TimeSpanExtensions.Min(firstSubtitleTime, injection.OffsetTime + injection.Duration),
                    injection.Lines));
            }
            else if (injection.Origin == SubtitleToInjectOrigin.End)
            {
                file.Subtitles.Add(new Subtitle(
                    TimeSpanExtensions.Max(lastSubtitleTime, offset + injection.OffsetTime),
                    TimeSpanExtensions.Max(lastSubtitleTime, offset + injection.OffsetTime + injection.Duration),
                    injection.Lines));
            }
        }
    }
}
