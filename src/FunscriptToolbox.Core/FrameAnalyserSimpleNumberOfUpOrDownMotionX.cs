using System.Linq;

namespace FunscriptToolbox.Core
{
    public partial class VerbMotionVectorsCreateFunscript
    {
        public class FrameAnalyserSimpleNumberOfUpOrDownMotionX : FrameAnalyser
        {
            public override string Name => "NumberOfUpOrDownMotionX";

            protected override int ComputeFrameValue(MotionVectorsFrame frame)
            {
                return frame.MotionsX.Sum(f => (sbyte)f < 0
                    ? -1
                    : (sbyte)f > 0
                    ? 1
                     : 0);
            }
        }
    }
}
