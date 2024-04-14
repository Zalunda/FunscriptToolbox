using System;

namespace FunscriptToolbox.Core
{
    public class Subtitle
    {
        public int? Number { get; }
        public TimeSpan StartTime { get; }
        public TimeSpan EndTime { get; }
        public string[] Lines { get; }
        public string Text => string.Join("\n", Lines);
        public TimeSpan Duration => this.EndTime - this.StartTime;

        public Subtitle(TimeSpan startTime, TimeSpan endTime, string line, int? number = null)
            : this(startTime, endTime, new[] { line }, number)
        {
        }

        public Subtitle(TimeSpan startTime, TimeSpan endTime, string[] lines, int? number = null)
        {
            Number = number;
            StartTime = startTime;
            EndTime = endTime;
            Lines = lines;
        }
    }
}
