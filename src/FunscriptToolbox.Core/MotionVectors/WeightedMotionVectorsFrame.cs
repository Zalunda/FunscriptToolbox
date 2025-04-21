namespace FunscriptToolbox.Core.MotionVectors
{
    public class WeightedMotionVectorsFrame
    {
        public MotionVectorsFrame<CellMotionSByte> Original { get; }
        public long Weight { get; }

        public WeightedMotionVectorsFrame(MotionVectorsFrame<CellMotionSByte> original, long weight)
        {
            Original = original;
            Weight = weight;
        }
    }
}
