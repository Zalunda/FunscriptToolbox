using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.Core.MotionVectors
{
    public class FrameDirectionAnalyser
    {
        public MotionVectorsFrameLayout FrameLayout { get; }
        public BlocDirectionRule[] Rules { get; }
        public int ActivityLevel { get; }
        public int QualityLevel { get; }

        public FrameDirectionAnalyser(
            MotionVectorsFrameLayout frameLayout,
            BlocDirectionRule[] rules = null,
            int activityLevel = 0, 
            int qualityLevel = 0)
        {
            this.FrameLayout = frameLayout;
            this.Rules = rules ?? Array.Empty<BlocDirectionRule>();
            this.ActivityLevel = activityLevel;
            this.QualityLevel = qualityLevel;
        }

        public FrameDirectionAnalyser Mask(double maskX, double maskY, double maskWidth, double maskHeight)
        {
            var filteredRules = new List<BlocDirectionRule>();

            // Get block dimensions and number of columns from FrameLayout
            int blockPixelWidth = this.FrameLayout.CellWidth;
            int blockPixelHeight = this.FrameLayout.CellWidth;
            int nbColumns = this.FrameLayout.NbColumns;

            // Pre-calculate mask boundaries
            double maskRight = maskX + maskWidth;
            double maskBottom = maskY + maskHeight;

            foreach (var rule in this.Rules)
            {
                // Calculate block's top-left corner coordinates (in pixels)
                // rule.Index is a flattened index: 0, 1, ..., NbColumns-1 for the first row,
                // NbColumns, ..., 2*NbColumns-1 for the second row, and so on.
                int blockCol = rule.Index % nbColumns;
                int blockRow = rule.Index / nbColumns; // Integer division gives the row index

                double blockPixelX = blockCol * blockPixelWidth;
                double blockPixelY = blockRow * blockPixelHeight;

                // Calculate block boundaries
                double blockRight = blockPixelX + blockPixelWidth;
                double blockBottom = blockPixelY + blockPixelHeight;

                // Standard AABB (Axis-Aligned Bounding Box) intersection test:
                // Two rectangles overlap if (and only if)
                // RectA.Left < RectB.Right AND
                // RectA.Right > RectB.Left AND
                // RectA.Top < RectB.Bottom AND
                // RectA.Bottom > RectB.Top
                bool overlaps = maskX < blockRight &&
                                maskRight > blockPixelX &&
                                maskY < blockBottom &&
                                maskBottom > blockPixelY;
                if (overlaps)
                {
                    filteredRules.Add(rule);
                }
            }

            return new FrameDirectionAnalyser(
                this.FrameLayout,       // FrameLayout itself doesn't change
                filteredRules.ToArray(),
                this.ActivityLevel,     // ActivityLevel is preserved
                this.QualityLevel       // QualityLevel is preserved
            );
        }

        public FrameDirectionAnalyser Filter(int activityLevel, int qualityLevel, double minPercentage)
        {
            var rules = this.Rules
                    .Where(rule => rule.Activity >= activityLevel)
                    .Where(rule => rule.Quality >= qualityLevel)
                    .ToArray();
            var minRules = (int)(this.FrameLayout.NbColumns * this.FrameLayout.NbRows * minPercentage / 100);
            if (rules.Length < minRules)
            {
                rules = this.Rules
                    .Where(rule => rule.Activity >= activityLevel)
                    .OrderByDescending(rule => rule.Quality)
                    .Take(minRules)
                    .ToArray();
            }
            return new FrameDirectionAnalyser(
                this.FrameLayout,       // FrameLayout itself doesn't change
                rules,
                this.ActivityLevel,     // ActivityLevel is preserved
                this.QualityLevel);     // QualityLevel is preserved
        }
    }
}
