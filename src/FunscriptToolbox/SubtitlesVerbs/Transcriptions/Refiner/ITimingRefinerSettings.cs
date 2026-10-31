using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>
    /// Settings consumed by <see cref="TimingRefiner"/>.
    /// TranscriberAIRefineTiming implements this directly, so the JSON config stays flat.
    /// </summary>
    public interface ITimingRefinerSettings
    {
        int MaxRetriesPerNode { get; }
        int MaxMountainSteps { get; }
        TimeSpan MaxExpansion { get; }
        TimeSpan BleedDetectionMargin { get; }
        TimeSpan TinyOverlapTolerance { get; }
        float ValleyReach { get; }
    }
}
