using System.Linq;

namespace FunscriptToolbox.Core
{ 
    public partial class VerbMotionVectorsCreateFunscript
    {
        public class FrameAnalyserSimpleNumberOfUpOrDownMotionY : FrameAnalyser
        {
            public override string Name => "NumberOfUpOrDownMotionY";

            protected override int ComputeFrameValue(MotionVectorsFrame frame)
            {
                return frame.MotionsY.Sum(f => (sbyte)f < 0 
                    ? -1 
                    : (sbyte)f > 0 
                    ? 1 
                     : 0);
            }
        }
    }
}
