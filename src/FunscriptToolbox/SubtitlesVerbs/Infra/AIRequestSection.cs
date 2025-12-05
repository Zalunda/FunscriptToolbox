using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    [Flags]
    public enum AIRequestSection
    {
        SystemPrompt = 1,
        SystemValidation = 2,
        ContextNodes = 4,
        PrimaryNodes = 8,
        Unspecifed = 16,
        Training = 32,
        FALLBACK = 1024
    }
}