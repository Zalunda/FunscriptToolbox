namespace FunscriptToolbox.Core.MotionVectors
{
    public struct BlocAnalyserRule
    {
        public ushort Index { get; }
        public byte Direction { get; }
        public float Activity { get; }
        public float Quality { get; }
        public float WeightTraveled { get; }

        public BlocAnalyserRule(
            ushort index,
            byte direction,
            float activity = 0,
            float quality = 0,
            float weightTraveled = 0)
        {
            this.Index = index;
            this.Direction = direction;
            this.Activity = activity;
            this.Quality = quality;
            this.WeightTraveled = weightTraveled;
        }
    }
}
