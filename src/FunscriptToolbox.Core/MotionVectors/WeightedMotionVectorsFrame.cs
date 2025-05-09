namespace FunscriptToolbox.Core.MotionVectors
{
    public class WeightedMotionVectorsFrame
    {
        public MotionVectorsFrame<CellMotionSByte> Original { get; }
        public long Weight { get; }
        public long PartialWeight { get; }

        public WeightedMotionVectorsFrame(MotionVectorsFrame<CellMotionSByte> original, long weight, long partialWeight)
        {
            Original = original;
            Weight = weight;
            PartialWeight = partialWeight;
        }
    }
}
