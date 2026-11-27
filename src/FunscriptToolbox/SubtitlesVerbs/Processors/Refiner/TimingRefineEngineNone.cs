using FunscriptToolbox.SubtitlesVerbs.Infra;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Processors.Refiner
{
    [ConfigDoc("The snap on the terrain of the audio, and nothing else: no AI request at all (free). Title card detection and Validation still work (expect many warnings: the text is never checked).",
        Advice = "The recommended choice for now: the timings are checked in the Review editor anyway.")]
    public class TimingRefineEngineNone : TimingRefineEngine
    {
        public override bool UsesAI => false;

        public override IEnumerable<AIOptions> InnerOptionsUsed => Array.Empty<AIOptions>();

        public override IReadOnlyList<TimingRefinePass> CreatePasses(TimingRefiner refiner) => Array.Empty<TimingRefinePass>();
    }
}
