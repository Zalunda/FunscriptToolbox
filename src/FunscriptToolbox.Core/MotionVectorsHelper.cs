using System;
using System.Diagnostics;
using System.Linq;

namespace FunscriptToolbox.Core
{
    public static class MotionVectorsHelper
    {
        public const int NbBaseAngles = 12;

        /// <summary>
        /// Lookup table. Given motionX as a byte (even if the value is handled as sbyte), motionY as a byte and the angle to compare it too.
        /// For example: 
        ///     FrameAnalyser.LookupMotionXMotionYAngle[(byte)motionX][(byte)motionY][BaseAngles.Right] would return the length motion compared to right (i.e. 3h on an analog clock)
        ///     FrameAnalyser.LookupMotionXMotionYAngle[12][-3][BaseAngles.Right] => 120 
        /// Note: the length is multiplied by 100 to have more precision. GetFinalMotionLength(-10,0,Up) => Returns 1000 instead of 10. The max value is 1749.
        /// </summary>
        static private short[,,] rs_lLookupMotionXYAndAngleToWeight;
        static private object rs_lLookupMotionXYAndAngleToWeightLock = new object();
        static public short[,,] GetLookupMotionXYAndAngleToWeightTable()
        {
            if (rs_lLookupMotionXYAndAngleToWeight == null)
            {
                lock (rs_lLookupMotionXYAndAngleToWeightLock)
                {
                    if (rs_lLookupMotionXYAndAngleToWeight == null)
                    {
                        // Note: If the dimensions of this is ever changed, check the usages because, for performance, it's often hardcoded.
                        rs_lLookupMotionXYAndAngleToWeight = new short[byte.MaxValue + 1, byte.MaxValue + 1, NbBaseAngles];
                        for (int xAsByte = 0; xAsByte <= byte.MaxValue; xAsByte++)
                        {
                            var x = (sbyte)xAsByte;
                            for (int yAsByte = 0; yAsByte <= byte.MaxValue; yAsByte++)
                            {
                                var y = (sbyte)yAsByte;
                                double length = Math.Sqrt(x * x + y * y);
                                double angle = Math.Atan2(y, x);

                                for (int i = 0; i < NbBaseAngles; i++)
                                {
                                    var baseAngle = (i + 3) * (2 * Math.PI / NbBaseAngles); // i + 3 is because we want to have the value 0 represente Up (like a clock)
                                    double diff = angle - baseAngle;
                                    rs_lLookupMotionXYAndAngleToWeight[xAsByte, yAsByte, i] = (short)Math.Round(10 * length * Math.Cos(diff));
                                }
                            }
                        }
                    }
                }
            }
            return rs_lLookupMotionXYAndAngleToWeight;
        }
    }
}