using System;

namespace AudioSynchronization
{
    public class CompareOptions
    {
        public TimeSpan MinimumMatchLength { get; set; }
        public int NbPeaksPerMinute { get; set; }

        public override string ToString()
        {
            return $"{MinimumMatchLength}, {NbPeaksPerMinute}";
        }
    }
}
