using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>Options for AudioTerrainMappingSimple.FindDip.</summary>
    public class DipSearch
    {
        public TimeSpan MinJump { get; set; } = TimeSpan.Zero;
        public bool StopAtSpeechEdge { get; set; } = false;
        public bool Inclusive { get; set; } = false;
        public TimeSpan? Limit { get; set; }
    }
}
