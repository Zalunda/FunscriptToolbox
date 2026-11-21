using FunscriptToolbox.SubtitlesVerbs.Infra;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>
    /// No refining after the initial snap on the terrain of the audio: no AI request at all (no tokens).
    /// The title cards and the validation still work (expect many warnings: the text is never checked).
    /// </summary>
    public class TimingRefineEngineNone : TimingRefineEngine
    {
        public override bool UsesAI => false;

        public override IEnumerable<AIOptions> InnerOptionsUsed => Array.Empty<AIOptions>();

        public override IReadOnlyList<TimingRefinePass> CreatePasses(TimingRefiner refiner) => Array.Empty<TimingRefinePass>();
    }
}
