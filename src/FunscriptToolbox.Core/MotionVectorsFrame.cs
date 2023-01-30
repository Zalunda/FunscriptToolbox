using System;

namespace FunscriptToolbox.Core
{
    public class MotionVectorsFrame
    {
        public int FrameNumber { get; }
        public TimeSpan FrameTimeInMs { get; }
        public byte[] MotionsX { get; }
        public byte[] MotionsY { get; }

        public MotionVectorsFrame(int frameNumber, TimeSpan frameTimeInMs, byte[] motionsX, byte[] motionsY)
        {
            FrameNumber = frameNumber;
            FrameTimeInMs = frameTimeInMs;
            MotionsX = motionsX;
            MotionsY = motionsY;
        }
    }
}