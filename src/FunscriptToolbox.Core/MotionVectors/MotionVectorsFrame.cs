using System;

namespace FunscriptToolbox.Core.MotionVectors
{
    public struct Motion 
    {
        public sbyte X;
        public sbyte Y;
    }

    public class MotionVectorsFrame
    {
        public int FrameNumber { get; }
        public TimeSpan FrameTime { get; }
        public Motion[] Motions { get; }

        public MotionVectorsFrame(int frameNumber, TimeSpan frameTime, Motion[] motions)
        {
            FrameNumber = frameNumber;
            FrameTime = frameTime;
            Motions = motions;
        }
    }
}