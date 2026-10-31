using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    public class TimingClipOption
    {
        public string FieldName { get; set; }
        public string StartId { get; set; }
        public string EndId { get; set; }
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
    }
}
