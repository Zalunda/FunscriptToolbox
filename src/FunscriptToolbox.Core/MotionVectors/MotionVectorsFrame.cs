using System;

namespace FunscriptToolbox.Core.MotionVectors
{
    public class MotionVectorsFrame
    {
        public int FrameNumber { get; }
        public TimeSpan FrameTime { get; }
        public char FrameType { get; }
        public byte[] MotionsX { get; }
        public byte[] MotionsY { get; }

        public MotionVectorsFrame(int frameNumber, TimeSpan frameTime, char frameType, byte[] motionsX, byte[] motionsY)
        {
            FrameNumber = frameNumber;
            FrameTime = frameTime;
            FrameType = frameType;
            MotionsX = motionsX;
            MotionsY = motionsY;
        }
    }
}