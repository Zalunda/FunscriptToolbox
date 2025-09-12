using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class TimelineSegment
    {
        public string Filename { get; }
        public TimeSpan Duration { get; }
        public TimeSpan Offset { get; }

        public TimelineSegment(string filename, TimeSpan duration, TimeSpan offset)
        {
            this.Filename = filename;
            this.Duration = duration;
            this.Offset = offset;
        }
    }
}