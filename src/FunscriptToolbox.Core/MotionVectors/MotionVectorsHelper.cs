using System;

namespace FunscriptToolbox.Core.MotionVectors
{
    public static class MotionVectorsHelper
    {
        public const int NbBaseDirection = 12;

        /// <summary>
        /// Lookup table. Given motionX as a byte (even if the value is handled as sbyte), motionY as a byte and the angle to compare it too.
        /// For example: 
        ///     FrameAnalyser.LookupMotionXMotionYDirection[(byte)motionX][(byte)motionY][3] would return the length motion compared to right (i.e. 3h on an analog clock)
        ///     FrameAnalyser.LookupMotionXMotionYDirection[12][-3][3] => 120 
        /// Note: the length is multiplied by 100 to have more precision. GetFinalMotionLength(-10,0,0) => Returns 1000 instead of 10. The max value is ~17500.
        /// </summary>
        static private short[,,] rs_lLookupMotionXYAndDirectionToWeight;
        static private object rs_lLookupMotionXYAndDirectionToWeightLock = new object();
        static public short[,,] GetLookupMotionXYAndDirectionToWeightTable()
        {
            if (rs_lLookupMotionXYAndDirectionToWeight == null)
            {
                lock (rs_lLookupMotionXYAndDirectionToWeightLock)
                {
                    if (rs_lLookupMotionXYAndDirectionToWeight == null)
                    {
                        // Note: If the dimensions of this is ever changed, check the usages because, for performance, it's often hardcoded.
                        rs_lLookupMotionXYAndDirectionToWeight = new short[byte.MaxValue + 1, byte.MaxValue + 1, NbBaseDirection];
                        for (int xAsByte = 0; xAsByte <= byte.MaxValue; xAsByte++)
                        {
                            var x = (sbyte)xAsByte;
                            for (int yAsByte = 0; yAsByte <= byte.MaxValue; yAsByte++)
                            {
                                var y = (sbyte)yAsByte;
                                double length = Math.Sqrt(x * x + y * y);
                                double angle = Math.Atan2(y, x);

                                for (int i = 0; i < NbBaseDirection; i++)
                                {
                                    var baseAngle = (i + 3) * (2 * Math.PI / NbBaseDirection); // i + 3 is because we want to have the value 0 represente Up (like a clock)
                                    double diff = angle - baseAngle;
                                    rs_lLookupMotionXYAndDirectionToWeight[xAsByte, yAsByte, i] = (short)Math.Round(100 * length * Math.Cos(diff));
                                }
                            }
                        }
                    }
                }
            }
            return rs_lLookupMotionXYAndDirectionToWeight;
        }
    }
}