using System;
using System.IO;

namespace FunscriptToolbox.Core.MotionVectors
{
    public interface ICellMotion
    {
        public int X { get; }
        public int Y { get; }
    }

    public struct CellMotionSByte : ICellMotion
    {
        public sbyte X;
        public sbyte Y;

        int ICellMotion.X => X;

        int ICellMotion.Y => Y;
    }

    public struct CellMotionInt : ICellMotion
    {
        public int X;
        public int Y;

        int ICellMotion.X => X;

        int ICellMotion.Y => Y;
    }

    public class MotionVectorsFrame<T> where T : ICellMotion
    {
        public MotionVectorsFrameLayout Layout { get; }
        public int NbRows => this.Layout.NbRows;
        public int NbColumns => this.Layout.NbColumns;
        public int NbCells => this.Layout.NbCellsTotalPerFrame;

        public int FrameNumber { get; }
        public TimeSpan FrameTime { get; }
        public char FrameType { get; }
        public T[] Motions { get; }

        public MotionVectorsFrame(MotionVectorsFrameLayout layout, int frameNumber, TimeSpan frameTime, char frameType, T[] motions)
        {
            Layout = layout;
            FrameNumber = frameNumber;
            FrameTime = frameTime;
            FrameType = frameType;
            Motions = motions;
        }

        public MotionVectorsFrame<CellMotionInt> Simplify(int newCellWidth, int newCellHeight)
        {
            int newNbColumns = (this.Layout.Width + newCellWidth - 1) / newCellWidth;
            int newNbRows = (this.Layout.Height + newCellHeight - 1) / newCellHeight;
            var newMetrics = new MotionVectorsFrameLayout(
                this.Layout.Width,
                this.Layout.Height,
                newCellWidth,
                newCellHeight,
                newNbColumns,
                newNbRows);

            var newMotions = new CellMotionInt[newNbColumns * newNbRows];

            for (int newRow = 0; newRow < newNbRows; newRow++)
            {
                for (int newCol = 0; newCol < newNbColumns; newCol++)
                {
                    float weightedSumX = 0;
                    float weightedSumY = 0;

                    // Calculate the pixel boundaries of the new cell
                    int startX = newCol * newCellWidth;
                    int endX = Math.Min(startX + newCellWidth, this.Layout.Width);
                    int startY = newRow * newCellHeight;
                    int endY = Math.Min(startY + newCellHeight, this.Layout.Height);

                    // Find original cells that overlap with this new cell
                    int startOldCol = startX / this.Layout.CellWidth;
                    int endOldCol = (endX - 1) / this.Layout.CellWidth;
                    int startOldRow = startY / this.Layout.CellHeight;
                    int endOldRow = (endY - 1) / this.Layout.CellHeight;

                    // Calculate weighted sum based on overlap area
                    for (int oldRow = startOldRow; oldRow <= endOldRow; oldRow++)
                    {
                        for (int oldCol = startOldCol; oldCol <= endOldCol; oldCol++)
                        {
                            // Calculate overlap area
                            int overlapStartX = Math.Max(startX, oldCol * this.Layout.CellWidth);
                            int overlapEndX = Math.Min(endX, (oldCol + 1) * this.Layout.CellWidth);
                            int overlapStartY = Math.Max(startY, oldRow * this.Layout.CellHeight);
                            int overlapEndY = Math.Min(endY, (oldRow + 1) * this.Layout.CellHeight);

                            int overlapWidth = overlapEndX - overlapStartX;
                            int overlapHeight = overlapEndY - overlapStartY;
                            float overlapArea = overlapWidth * overlapHeight;
                            float weight = overlapArea / (newCellWidth * newCellHeight);

                            var oldMotion = Motions[oldRow * NbColumns + oldCol];
                            weightedSumX += oldMotion.X * weight;
                            weightedSumY += oldMotion.Y * weight;
                        }
                    }

                    newMotions[newRow * newNbColumns + newCol] = new CellMotionInt
                    {
                        X = (int)weightedSumX,
                        Y = (int)weightedSumY
                    };
                }
            }

            return new MotionVectorsFrame<CellMotionInt>(
                newMetrics,
                FrameNumber,
                FrameTime,
                FrameType,
                newMotions);
        }

        public void WriteDebugFileAsciiNumerical(string suffix = "")
        {
            using (var writer = File.CreateText($"FRAME-NUM{suffix}{this.FrameNumber:D8}-{(int)this.FrameTime.TotalMinutes:D3}{this.FrameTime.Seconds:D2}{this.FrameTime.Milliseconds:D3}.txt"))
            {
                var index = 0;
                for (var row = 0; row < this.NbRows; row++)
                {
                    for (var column = 0; column < this.NbColumns; column++)
                    {
                        var motion = this.Motions[index++];
                        writer.Write((motion.X != 0 || motion.Y != 0)
                            ? $"{motion.X,4},{motion.Y,4}|"
                            : "         |");
                    }
                    writer.WriteLine();
                }
            }
        }

        public void DebugFileAsciiSimple()
        {
            using (var writer = File.CreateText($"FRAME-SIMPLE-{this.FrameNumber:D8}-{(int)this.FrameTime.TotalMinutes:D3}{this.FrameTime.Seconds:D2}{this.FrameTime.Milliseconds:D3}.txt"))
            {
                var index = 0;
                for (int row = 0; row < this.NbRows; row++)
                {
                    for (int column = 0; column < this.NbColumns; column++)
                    {
                        // Get the motion value at current position, similar to how the original code processes cells
                        var motionY = this.Motions[index++].Y;

                        if (motionY > 0)
                            writer.Write("+");
                        else if (motionY < 0)
                            writer.Write("-");
                        else
                            writer.Write(" ");
                    }
                    writer.WriteLine(); // New line after each row, similar to the grid structure in
                }
            }
        }
    }
}