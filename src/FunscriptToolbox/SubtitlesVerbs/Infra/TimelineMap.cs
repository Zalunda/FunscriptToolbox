using FunscriptToolbox.Core.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class TimelineMap
    { 
        public TimelineSegment[] Segments { get; }
        public TimeSpan Duration { get; }
        [JsonIgnore]
        public bool IsMultipart => Segments.Length > 1;

        public TimelineMap(string[] filenames)
        {
            this.Segments = filenames.Select(filename => new TimelineSegment(
                Path.GetFileName(filename), 
                TimeSpan.Zero, 
                TimeSpan.Zero)).ToArray();
            this.Duration = TimeSpan.Zero;
        }

        [JsonConstructor]
        public TimelineMap(TimelineSegment[] segments)
        {
            this.Segments = segments;
            this.Duration = segments.Sum(f => f.Duration);
        }

        public IEnumerable<string> GetFullPaths(string parentPath) => this.Segments.Select(segment => Path.Combine(parentPath, segment.Filename));

        public (int index, string filename, TimeSpan newPosition) GetPathAndPosition(TimeSpan position)
        {
            var index = 1;
            foreach (var segment in this.Segments)
            {
                if (position > segment.Offset && position < segment.Offset + segment.Duration)
                {
                    return (index, segment.Filename, position - segment.Offset);
                }
                index++;
            }

            var lastSegment = this.Segments.Last();
            return (index, lastSegment.Filename, position - lastSegment.Offset);
        }

        public string ConvertToPartSpecificFileIndexAndTime(TimeSpan globalTime)
        {
            var (index, _, newTime) = GetPathAndPosition(globalTime);
            return this.IsMultipart
                ? $"{index}, {newTime:hh\\:mm\\:ss\\.fff}"
                : $"{globalTime:hh\\:mm\\:ss\\.fff}";
        }
    }
}