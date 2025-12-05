using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    [Flags]
    public enum AIResponseSection
    {
        Thoughts = 1,
        Candidates = 2,
        FALLBACK = 1024
    }
}