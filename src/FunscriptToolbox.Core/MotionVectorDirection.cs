namespace FunscriptToolbox.Core
{
    // The base angles are based on something that look like a clock with 16h instead of 12.
    public enum MotionVectorDirection : int
    {
        Up = 0,
        UpRightUp = 1,
        UpRight = 2,
        UpRightDown = 3,
        Right = 4,
        DownRightUp = 5,
        DownRight = 6,
        DownRightDown = 7,
        Down = 8,
        DownLeftDown = 9,
        DownLeft = 10,
        DownLeftUp = 11,
        Left = 12,
        UpLeftDown = 13,
        UpLeft = 14,
        UpLeftUp = 15,
    }
}
