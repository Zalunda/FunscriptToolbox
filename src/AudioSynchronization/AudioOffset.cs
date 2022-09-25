using System;

namespace AudioSynchronization
{
    public class AudioOffset
    {
        public AudioOffset(TimeSpan start, TimeSpan end, TimeSpan offset)
        {
            Start = start;
            End = end;
            Offset = offset;
        }

        public TimeSpan Start { get; }
        public TimeSpan End { get; }
        public TimeSpan Offset { get; }

        public override string ToString()
        {
            return $"{this.Start};{this.End};{this.Offset}";
        }
    }
}