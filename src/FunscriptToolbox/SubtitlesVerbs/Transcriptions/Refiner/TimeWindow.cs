using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    public class TimeWindow
    {
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public TimingMove Kind { get; set; }
        public int ParentIndex { get; set; } = -1;

        [JsonIgnore] public TimeSpan Duration => End - Start;

        public TimeWindow() { }

        public TimeWindow(TimeSpan start, TimeSpan end, TimingMove kind = TimingMove.Init, int parentIndex = -1)
        {
            Start = start;
            End = end;
            Kind = kind;
            ParentIndex = parentIndex;
        }

        public TimeSpan Boundary(BoundarySide side) => side == BoundarySide.Start ? Start : End;
    }
}
