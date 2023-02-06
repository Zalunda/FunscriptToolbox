using System;

namespace FunscriptToolbox.Core.MotionVectors
{
    public class MotionVectorsFrame
    {
        public int FrameNumber { get; }
        public TimeSpan FrameTimeInMs { get; }
        public char FrameType { get; }
        public byte[] MotionsX { get; }
        public byte[] MotionsY { get; }

        public MotionVectorsFrame(int frameNumber, TimeSpan frameTimeInMs, char frameType, byte[] motionsX, byte[] motionsY)
        {
            FrameNumber = frameNumber;
            FrameTimeInMs = frameTimeInMs;
            FrameType = frameType;
            MotionsX = motionsX;
            MotionsY = motionsY;
        }
    }
}