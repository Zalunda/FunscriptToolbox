using System;

namespace FunscriptToolbox.SubtitlesVerbs.Processors.Refiner
{
    [ConfigDoc("One simulated run (see Simulations): the settings that are not the same as the real ones. Not set (null, or -1 for the numbers): the real value.")]
    public class TimingSimulationVariant
    {
        [ConfigDoc("Its name in the report.")]
        public string Name { get; set; }
        [ConfigDoc("Replaces MaxExpansion of the engine (TimingRefineEngineStartEndJudge).")]
        public TimeSpan? MaxExpansion { get; set; }
        [ConfigDoc("Replaces MaxDipsInward of the engine (-1: the real value).")]
        public int MaxDipsInward { get; set; } = -1;
        [ConfigDoc("Replaces MaxDipsOutward of the engine (-1: the real value).")]
        public int MaxDipsOutward { get; set; } = -1;
        [ConfigDoc("Replaces MaxTestsInRequest of the engine (-1: the real value).")]
        public int MaxTestsInRequest { get; set; } = -1;
        [ConfigDoc("Replaces StartPassTailPadding of the engine.")]
        public TimeSpan? StartPassTailPadding { get; set; }
        [ConfigDoc("Replaces BleedDetectionMargin of RefineTiming.")]
        public TimeSpan? BleedDetectionMargin { get; set; }
        [ConfigDoc("Replaces TrimSilenceOnStart of RefineTiming.")]
        public bool? TrimSilenceOnStart { get; set; }
        [ConfigDoc("Replaces TrimSilenceOnEnd of RefineTiming.")]
        public bool? TrimSilenceOnEnd { get; set; }
        [ConfigDoc("Replaces ValleyReach of RefineTiming.")]
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