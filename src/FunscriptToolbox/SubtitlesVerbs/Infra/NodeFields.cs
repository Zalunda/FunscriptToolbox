using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    [Flags]
    public enum NodeFields
    {
        StartTime = 1,
        EndTime = 2,
        Duration = 4
    }
}