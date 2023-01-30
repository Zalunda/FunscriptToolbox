namespace FunscriptToolbox.Core
{
    public struct FrameAnalyserRule
    { 
        public int Index { get; }

        public MotionVectorDirection Direction { get; }

        public FrameAnalyserRule(int index, MotionVectorDirection direction)
        {
            this.Index = index;
            this.Direction = direction;
        }
    }
}
