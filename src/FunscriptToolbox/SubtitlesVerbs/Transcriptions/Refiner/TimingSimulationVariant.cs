using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>
    /// Settings overrides for one simulated run (null = same as the real settings).
    /// </summary>
    public class TimingSimulationVariant
    {
        public string Name { get; set; }
        public TimeSpan? MaxExpansion { get; set; }
        public int MaxDipsInward { get; set; } = -1;
        public int MaxDipsOutward { get; set; } = -1;
        public int MaxTestsInRequest { get; set; } = -1;
        public TimeSpan? StartPassTailPadding { get; set; }
        public TimeSpan? BleedDetectionMargin { get; set; }
        public bool? TrimSilenceOnStart { get; set; }
        public bool? TrimSilenceOnEnd { get; set; }
        public float? ValleyReach { get; set; }

        public TimingRefinerSettings Apply(TimingRefinerSettings b) => new TimingRefinerSettings
        {
            Mode = RefineMode.Full,
            SourceAudioIdForTerrain = b.SourceAudioIdForTerrain,
            ValleyReach = ValleyReach ?? b.ValleyReach,
            StartPassTailPadding = StartPassTailPadding ?? b.StartPassTailPadding,
            MaxExpansion = MaxExpansion ?? b.MaxExpansion,
            MaxDipsInward = MaxDipsInward >= 0 ? MaxDipsInward : b.MaxDipsInward,
            MaxDipsOutward = MaxDipsOutward >= 0 ? MaxDipsOutward : b.MaxDipsOutward,
            MaxTestsInRequest = MaxTestsInRequest >= 0 ? MaxTestsInRequest : b.MaxTestsInRequest,
            BleedDetectionMargin = BleedDetectionMargin ?? b.BleedDetectionMargin,
            TrimSilenceOnStart = TrimSilenceOnStart ?? b.TrimSilenceOnStart,
            TrimSilenceOnEnd = TrimSilenceOnEnd ?? b.TrimSilenceOnEnd,
            InnerOptionsStart = b.InnerOptionsStart,
            InnerOptionsEnd = b.InnerOptionsEnd,
        };
    }
}