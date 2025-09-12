﻿using FunscriptToolbox.Core.Infra;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class TimelineMap
    { 
        public TimelineSegment[] Segments { get; }
        public TimeSpan Duration { get; }

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

        public (string filename, TimeSpan newPosition) GetPathAndPosition(TimeSpan position)
        {
            foreach (var segment in this.Segments)
            {
                if (position > segment.Offset && position < segment.Offset + segment.Duration)
                {
                    return (segment.Filename, position - segment.Offset);
                }
            }

            var lastSegment = this.Segments.Last();
            return (lastSegment.Filename, position - lastSegment.Offset);
        }
    }
}