using System;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public interface ITiming
    {
        public TimeSpan StartTime { get; }
        public TimeSpan EndTime { get; }
    }
}
