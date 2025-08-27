using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public interface ITiming
    {
        public TimeSpan StartTime { get; }
        public TimeSpan EndTime { get; }
    }
}
