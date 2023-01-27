using System.Linq;

namespace FunscriptToolbox.Core
{
    public partial class VerbMotionVectorsCreateFunscript
    {
        public class FrameAnalyserSimpleSumMotionX : FrameAnalyser
        {
            public override string Name => "SumMotionX";

            protected override int ComputeFrameValue(MotionVectorsFrame frame)
            {
                return frame.MotionsX.Sum(f => (sbyte)f);
            }
        }
    }
}
