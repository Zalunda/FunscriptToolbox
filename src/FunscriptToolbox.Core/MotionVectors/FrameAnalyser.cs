using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.Core.MotionVectors
{
    public class FrameAnalyser
    {
        public MotionVectorsFrameLayout FrameLayout { get; }

        /// <summary>
        /// Analyser optimized for detecting the main/coarse part of movements
        /// </summary>
        public FrameDirectionAnalyser CoarseAnalyser { get; }

        /// <summary>
        /// Analyser optimized for detecting UP to DOWN transitions (peaks)
        /// </summary>
        public FrameDirectionAnalyser PeaksAnalyser { get; }

        /// <summary>
        /// Analyser optimized for detecting DOWN to UP transitions (valleys)
        /// </summary>
        public FrameDirectionAnalyser ValleysAnalyser { get; }

        public FrameAnalyser(
            MotionVectorsFrameLayout frameLayout,
            FrameDirectionAnalyser coarseAnalyser,
            FrameDirectionAnalyser peaksAnalyser,
            FrameDirectionAnalyser valleysAnalyser)
        {
            FrameLayout = frameLayout;
            CoarseAnalyser = coarseAnalyser;
            PeaksAnalyser = peaksAnalyser;
            ValleysAnalyser = valleysAnalyser;
        }

        /// <summary>
        /// Creates a new collection with masks applied to all analysers
        /// </summary>
        public FrameAnalyser Mask(double maskX, double maskY, double maskWidth, double maskHeight)
        {
            return new FrameAnalyser(
                FrameLayout,
                CoarseAnalyser.Mask(maskX, maskY, maskWidth, maskHeight),
                PeaksAnalyser.Mask(maskX, maskY, maskWidth, maskHeight),
                ValleysAnalyser.Mask(maskX, maskY, maskWidth, maskHeight));
        }

        /// <summary>
        /// Creates a new collection with filters applied to all analysers
        /// </summary>
        public FrameAnalyser Filter(int activityLevel, int qualityLevel, double minPercentage)
        {
            return new FrameAnalyser(
                FrameLayout,
                CoarseAnalyser.Filter(activityLevel, qualityLevel, minPercentage),
                PeaksAnalyser.Filter(activityLevel, qualityLevel, minPercentage),
                ValleysAnalyser.Filter(activityLevel, qualityLevel, minPercentage));
        }

        public FunscriptActionExtended[] GenerateActions(
            MotionVectorsFileReader mvsReader,
            TimeSpan startingTime,
            TimeSpan endTime,
            GenerateActionsSettings settings)
        {
            var points = new List<FunscriptActionExtended>();

            var previousFrameTime = TimeSpan.Zero;
            var currentActionStartTime = TimeSpan.MinValue;
            var currentActionEndTime = TimeSpan.Zero;
            var lastFrameTotalWeight = 0L;
            var minimumActionDuration = TimeSpan.FromMilliseconds(1000.0 / (settings.MaximumStrokesDetectedPerSecond * 2) - 20);
            var framesWithSameDirectionAccumulator = new List<WeightedMotionVectorsFrameEx>();

            // Track which analyser detected the direction for the current movement
            foreach (var frame in mvsReader.ReadFrames(startingTime, endTime))
            {
                // Compute weights using all three analysers
                var coarseWeight = ComputeFrameTotalWeight(CoarseAnalyser, frame, out var coarsePartialWeight);
                var peaksWeight = ComputeFrameTotalWeight(PeaksAnalyser, frame, out var peaksPartialWeight);
                var valleysWeight = ComputeFrameTotalWeight(ValleysAnalyser, frame, out var valleysPartialWeight);

                // Determine the primary direction from the coarse analyser
                var totalWeight = coarseWeight;
                var partialWeight = coarsePartialWeight;

                var currentFrameWithWeight = new WeightedMotionVectorsFrameEx(
                    frame,
                    totalWeight,
                    partialWeight,
                    coarseWeight,
                    peaksWeight,
                    valleysWeight);

                if (currentActionStartTime < TimeSpan.Zero)
                {
                    currentActionStartTime = frame.FrameTime;
                }
                if (previousFrameTime == TimeSpan.Zero)
                {
                    previousFrameTime = frame.FrameTime;
                }

                if (currentFrameWithWeight.Weight == 0 // i.e. probably one of the rare I-frame
                    || (currentFrameWithWeight.Weight > 0 && lastFrameTotalWeight > 0)
                    || (currentFrameWithWeight.Weight < 0 && lastFrameTotalWeight < 0))
                {
                    // Same direction...
                    framesWithSameDirectionAccumulator.Add(currentFrameWithWeight);
                    currentActionEndTime = frame.FrameTime;
                }
                else
                {
                    // Direction has changed...
                    if (currentActionEndTime - currentActionStartTime >= minimumActionDuration)
                    {
                        AddPoints(
                            points,
                            currentActionStartTime,
                            currentActionEndTime,
                            lastFrameTotalWeight,
                            framesWithSameDirectionAccumulator,
                            settings);
                    }

                    lastFrameTotalWeight = currentFrameWithWeight.Weight;
                    currentActionStartTime = previousFrameTime;

                    framesWithSameDirectionAccumulator.Clear();
                    framesWithSameDirectionAccumulator.Add(currentFrameWithWeight);
                }
                previousFrameTime = frame.FrameTime;
            }

            // Handle the last movement if any
            if (framesWithSameDirectionAccumulator.Count > 0 && currentActionEndTime - currentActionStartTime >= minimumActionDuration)
            {
                AddPoints(
                    points,
                    currentActionStartTime,
                    currentActionEndTime,
                    lastFrameTotalWeight,
                    framesWithSameDirectionAccumulator,
                    settings);
            }

            // Normalize positions based on weights
            NormalizePositions(points);

            return points.ToArray();
        }

        private void AddPoints(
            List<FunscriptActionExtended> points,
            TimeSpan currentActionStartTime,
            TimeSpan currentActionEndTime,
            long lastFrameTotalWeight,
            List<WeightedMotionVectorsFrameEx> framesWithSameDirectionAccumulator,
            GenerateActionsSettings settings)
        {
            int startValue = (lastFrameTotalWeight > 0) ? 0 : 100;
            int endValue = (lastFrameTotalWeight > 0) ? 100 : 0;

            // Determine which transition analyser to use based on movement direction
            bool isValleyTransition = lastFrameTotalWeight > 0;

            // Create the action points
            var startPoint = GetOrAddPoint(points, FunscriptActionExtended.TimeToAt(currentActionStartTime), startValue);
            var endPoint = GetOrAddPoint(points, FunscriptActionExtended.TimeToAt(currentActionEndTime), endValue);

            // For finding the precise transition points, we use different strategies:
            // - Start of movement: use the transition analyser for the previous direction change
            // - End of movement: use the transition analyser for the current direction change

            // Find the start point using transition analyser
            var startMouvAt = FindMovementBoundary(
                    framesWithSameDirectionAccumulator,
                    (p) => isValleyTransition ? p.ValleysWeight : p.PeaksWeight,
                    isStart: true,
                    settings);

            // Find the end point using transition analyser  
            var endMouvAt = FindMovementBoundary(
                    framesWithSameDirectionAccumulator,
                    (p) => isValleyTransition ? p.PeaksWeight : p.ValleysWeight,
                    isStart: false,
                    settings);

            // Calculate weights from the coarse analyser for intensity
            var frameCountToKeep = Math.Max(2, (int)Math.Ceiling(framesWithSameDirectionAccumulator.Count * (double)settings.PercentageOfFramesToKeep / 100));
            var weight = 0L;
            var partialWeight = 0L;
            foreach (var frameX in framesWithSameDirectionAccumulator
                .OrderByDescending(f => Math.Abs(f.CoarseWeight))
                .Take(frameCountToKeep))
            {
                weight += frameX.CoarseWeight;
                partialWeight += frameX.Weight > 0 ?
                    Math.Max(0, frameX.PartialWeight) :
                    Math.Min(0, frameX.PartialWeight);
            }

            endPoint.SetWeight(weight, partialWeight);
        }

        /// <summary>
        /// Finds the precise boundary of a movement using the appropriate transition analyser
        /// </summary>
        private int FindMovementBoundary(
            List<WeightedMotionVectorsFrameEx> frames,
            Func<WeightedMotionVectorsFrameEx, long> weightSelector,
            bool isStart,
            GenerateActionsSettings settings)
        {
            if (frames.Count == 0)
            {
                return 0;
            }

            // Calculate how many frames to consider for transition detection
            var transitionFrameCount = Math.Max(1, Math.Min(frames.Count / 3,
                (int)(frames.Count * settings.PercentageOfFramesToKeep / 100.0)));

            IEnumerable<WeightedMotionVectorsFrameEx> transitionFrames;
            if (isStart)
            {
                // For start, look at the first portion of frames
                transitionFrames = frames.Take(transitionFrameCount);
            }
            else
            {
                // For end, look at the last portion of frames
                transitionFrames = frames.Skip(Math.Max(0, frames.Count - transitionFrameCount));
            }

            // Find the frame with the strongest transition signal
            var bestFrame = transitionFrames
                .OrderByDescending(f => Math.Abs(weightSelector(f)))
                .FirstOrDefault();

            if (bestFrame != null)
            {
                return FunscriptActionExtended.TimeToAt(bestFrame.Original.FrameTime);
            }

            // Fallback to first/last frame
            var fallbackFrame = isStart ? frames.First() : frames.Last();
            return FunscriptActionExtended.TimeToAt(fallbackFrame.Original.FrameTime);
        }

        private FunscriptActionExtended GetOrAddPoint(List<FunscriptActionExtended> points, int at, int pos)
        {
            var previousPoint = points.LastOrDefault();
            if (previousPoint?.At == at && previousPoint?.Pos == pos)
            {
                return previousPoint;
            }

            var newPoint = new FunscriptActionExtended(at, pos);
            points.Add(newPoint);
            return newPoint;
        }

        private void NormalizePositions(List<FunscriptActionExtended> points)
        {
            var sortedWeight = points
                .Where(point => point.Pos == 100)
                .Select(point => Math.Abs(point.PartialWeight))
                .OrderBy(w => w)
                .ToArray();

            var targetWeight = (sortedWeight.Length > 0)
                ? sortedWeight[sortedWeight.Length * 9 / 10]
                : 0;

            foreach (var point in points.Where(point => point.Pos == 100))
            {
                point.Pos = Math.Max(1, Math.Min(100,
                    (int)((double)100 * Math.Abs(point.Weight) / targetWeight)));
            }
        }

        private long ComputeFrameTotalWeight(
            FrameDirectionAnalyser analyser,
            MotionVectorsFrame<CellMotionSByte> frame,
            out long partialWeight)
        {
            var lookup = MotionVectorsHelper.GetLookupMotionXYAndDirectionToWeightTable();

            long totalWeight = 0;
            partialWeight = 0;

            if (analyser.Rules.Length == 0)
            {
                return 0;
            }

            var partialWeights = new short[analyser.Rules.Length];
            var i = 0;
            foreach (var rule in analyser.Rules)
            {
                var weight = lookup[(byte)frame.Motions[rule.Index].X, (byte)frame.Motions[rule.Index].Y, rule.Direction];
                partialWeights[i++] = weight;
                totalWeight += weight;
            }

            if (totalWeight > 0)
            {
                for (int k = 0; k < partialWeights.Length; k++)
                {
                    if (partialWeights[k] > 0)
                        partialWeight += partialWeights[k];
                }
            }
            else
            {
                for (int k = 0; k < partialWeights.Length; k++)
                {
                    if (partialWeights[k] < 0)
                        partialWeight += partialWeights[k];
                }
            }

            return totalWeight;
        }
    }

    /// <summary>
    /// Extended version of WeightedMotionVectorsFrame that includes weights from all three analysers
    /// </summary>
    public class WeightedMotionVectorsFrameEx
    {
        public MotionVectorsFrame<CellMotionSByte> Original { get; }

        /// <summary>
        /// Primary weight (from coarse analyser, used for direction detection)
        /// </summary>
        public long Weight { get; }

        /// <summary>
        /// Partial weight for intensity calculation
        /// </summary>
        public long PartialWeight { get; }

        /// <summary>
        /// Weight calculated using the coarse movement analyser
        /// </summary>
        public long CoarseWeight { get; }

        /// <summary>
        /// Weight calculated using the up-to-down transition analyser (peaks)
        /// </summary>
        public long PeaksWeight { get; }

        /// <summary>
        /// Weight calculated using the down-to-up transition analyser (valleys)
        /// </summary>
        public long ValleysWeight { get; }

        public WeightedMotionVectorsFrameEx(
            MotionVectorsFrame<CellMotionSByte> original,
            long weight,
            long partialWeight,
            long coarseWeight,
            long peaksWeight,
            long valleysWeight)
        {
            Original = original;
            Weight = weight;
            PartialWeight = partialWeight;
            CoarseWeight = coarseWeight;
            PeaksWeight = peaksWeight;
            ValleysWeight = valleysWeight;
        }
    }
}