using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>
    /// One audio clip sent to the AI. In a Start round only the start differs between clips,
    /// in an End round only the end differs.
    /// </summary>
    public class TimingClipOption
    {
        /// <summary>Name of the field sent to the AI: Audio_Max, Audio_S1.., Audio_E1..</summary>
        public string FieldName { get; set; }

        /// <summary>Option id the AI answers with: Max, S1.., E1..</summary>
        public string OptionId { get; set; }

        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
    }
}
