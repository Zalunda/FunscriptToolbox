namespace FunscriptToolbox.Core.MotionVectors
{
    public class MotionVectorsFrameLayout
    {
        public int Width { get; }
        public int Height { get; }
        public int CellWidth { get; }
        public int CellHeight { get; }
        public int NbColumns { get; }
        public int NbRows { get; }
        public int NbCellsTotalPerFrame { get; }

        public MotionVectorsFrameLayout(int width, int height, int cellWidth, int cellHeight, int nbColumns, int nbRows)
        {
            this.Width = width;
            this.Height = height;
            this.CellWidth = cellWidth;
            this.CellHeight = cellHeight;
            this.NbColumns = nbColumns;
            this.NbRows = nbRows;
            this.NbCellsTotalPerFrame = nbColumns * nbRows;
        }
    }
}