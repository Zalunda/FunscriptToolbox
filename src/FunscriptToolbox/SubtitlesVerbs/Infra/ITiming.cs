using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public interface ITiming
    {
        public TimeSpan StartTime { get; }
        public TimeSpan EndTime { get; }
        public TimeSpan Duration { get; }
    }

    public class Timing : ITiming
    {
        public Timing(TimeSpan startTime, TimeSpan endTime)
        {
            StartTime = startTime;
            EndTime = endTime;
        }

        public TimeSpan StartTime { get; }

        public TimeSpan EndTime { get; }

        public TimeSpan Duration => this.EndTime - this.StartTime;
    }
}
