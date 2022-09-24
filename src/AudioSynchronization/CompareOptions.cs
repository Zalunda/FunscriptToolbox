namespace AudioSynchronization
{
    public class CompareOptions
    {
        public int MinimumMatchLength { get; set; }
        public int NbPeaksPerMinute { get; set; }

        public override string ToString()
        {
            return $"{MinimumMatchLength}, {NbPeaksPerMinute}";
        }
    }
}
