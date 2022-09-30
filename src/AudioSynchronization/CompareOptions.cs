using System;

namespace AudioSynchronization
{
    public class CompareOptions
    {
        public TimeSpan MinimumMatchLength { get; set; }
        public int NbLocationsPerMinute { get; set; }

        public override string ToString()
        {
            return $"{MinimumMatchLength}, {NbLocationsPerMinute}";
        }
    }
}
