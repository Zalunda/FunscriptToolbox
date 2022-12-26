using System;

namespace FunscriptToolbox.Core
{
    public class Subtitle
    {
        public TimeSpan StartTime { get; }
        public TimeSpan EndTime { get; }
        public string[] Lines { get; }
        public TimeSpan Duration => this.EndTime - this.StartTime;

        public Subtitle(TimeSpan startTime, TimeSpan endTime, string line)
            : this(startTime, endTime, new[] { line })
        {
        }

        public Subtitle(TimeSpan startTime, TimeSpan endTime, string[] lines)
        {
            StartTime = startTime;
            EndTime = endTime;
            Lines = lines;
        }
    }
}
