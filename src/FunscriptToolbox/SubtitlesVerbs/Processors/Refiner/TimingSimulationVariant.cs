using System;

namespace FunscriptToolbox.SubtitlesVerbs.Processors.Refiner
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

        /// <summary>The settings of the variant: the real ones (their engine must be a StartEndJudge), with the overrides of the variant.</summary>
        public TimingRefinerSettings Apply(TimingRefinerSettings b)
        {
            var judge = b.Engine as TimingRefineEngineStartEndJudge
                ?? throw new InvalidOperationException("The simulations replay the engine 'TimingRefineEngineStartEndJudge' only.");
            return new TimingRefinerSettings
            {
                Enabled = true,
                SourceAudioIdForTerrain = b.SourceAudioIdForTerrain,
                ValleyReach = ValleyReach ?? b.ValleyReach,
                BleedDetectionMargin = BleedDetectionMargin ?? b.BleedDetectionMargin,
                TrimSilenceOnStart = TrimSilenceOnStart ?? b.TrimSilenceOnStart,
                TrimSilenceOnEnd = TrimSilenceOnEnd ?? b.TrimSilenceOnEnd,
                // (no padding and no tail check: like before, the simulation only counts the requests and the audio of the two passes)
                Engine = new TimingRefineEngineStartEndJudge
                {
                    InnerOptionsStart = judge.InnerOptionsStart,
                    InnerOptionsEnd = judge.InnerOptionsEnd,
                    StartPassTailPadding = StartPassTailPadding ?? judge.StartPassTailPadding,
                    MaxExpansion = MaxExpansion ?? judge.MaxExpansion,
                    MaxDipsInward = MaxDipsInward >= 0 ? MaxDipsInward : judge.MaxDipsInward,
                    MaxDipsOutward = MaxDipsOutward >= 0 ? MaxDipsOutward : judge.MaxDipsOutward,
                    MaxTestsInRequest = MaxTestsInRequest >= 0 ? MaxTestsInRequest : judge.MaxTestsInRequest,
                }
            };
        }
    }
}