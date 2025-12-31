using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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

            var currentMovementFrames = new List<WeightedMotionVectorsFrameEx>();
            var previousMovementFrames = new List<WeightedMotionVectorsFrameEx>();
            foreach (var frame in mvsReader.ReadFrames(startingTime, endTime))
            {
                var coarseWeight = ComputeFrameTotalWeight(CoarseAnalyser, frame, out var coarsePartialWeight);
                var peaksWeight = ComputeFrameTotalWeight(PeaksAnalyser, frame, out var peaksPartialWeight);
                var valleysWeight = ComputeFrameTotalWeight(ValleysAnalyser, frame, out var valleysPartialWeight);

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

                if (currentFrameWithWeight.Weight == 0
                    || (currentFrameWithWeight.Weight > 0 && lastFrameTotalWeight > 0)
                    || (currentFrameWithWeight.Weight < 0 && lastFrameTotalWeight < 0))
                {
                    // Same direction...
                    currentMovementFrames.Add(currentFrameWithWeight);
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
                            previousMovementFrames,
                            currentMovementFrames,
                            settings);
                    }

                    lastFrameTotalWeight = currentFrameWithWeight.Weight;
                    currentActionStartTime = previousFrameTime;

                    // Move current frames to previous, start fresh for current
                    previousMovementFrames.Clear();
                    previousMovementFrames.AddRange(currentMovementFrames);
                    currentMovementFrames.Clear();
                    currentMovementFrames.Add(currentFrameWithWeight);
                }
                previousFrameTime = frame.FrameTime;
            }

            // Handle the last movement
            if (currentMovementFrames.Count > 0 && currentActionEndTime - currentActionStartTime >= minimumActionDuration)
            {
                AddPoints(
                    points,
                    currentActionStartTime,
                    currentActionEndTime,
                    lastFrameTotalWeight,
                    previousMovementFrames,
                    currentMovementFrames,
                    settings);
            }

            NormalizePositions(points);
            return points.ToArray();
        }

        private void AddPoints(
            List<FunscriptActionExtended> points,
            TimeSpan currentActionStartTime,
            TimeSpan currentActionEndTime,
            long lastFrameTotalWeight,
            List<WeightedMotionVectorsFrameEx> beforeTransitionFrames,
            List<WeightedMotionVectorsFrameEx> afterTransitionFrames,
            GenerateActionsSettings settings)
        {
            int startValue = (lastFrameTotalWeight > 0) ? 0 : 100;
            int endValue = (lastFrameTotalWeight > 0) ? 100 : 0;

            // Determine transition type based on current movement direction
            // If current movement is UP (weight > 0), the START is a valley (DOWN→UP transition)
            // If current movement is DOWN (weight < 0), the START is a peak (UP→DOWN transition)
            bool currentIsUpMovement = lastFrameTotalWeight > 0;

            var startPoint = GetOrAddPoint(points, FunscriptActionExtended.TimeToAt(currentActionStartTime), startValue);
            var endPoint = GetOrAddPoint(points, FunscriptActionExtended.TimeToAt(currentActionEndTime), endValue);

            // Find start transition point:
            // - For UP movement: start is a valley, use ValleysAnalyser
            // - For DOWN movement: start is a peak, use PeaksAnalyser
            // Look at END of previous movement + START of current movement
            var startMouvAt = FindMovementBoundary(
                beforeTransitionFrames,
                afterTransitionFrames,
                (p) => currentIsUpMovement ? p.ValleysWeight : p.PeaksWeight,
                settings);

            // Find end transition point:
            // - For UP movement: end is a peak, use PeaksAnalyser  
            // - For DOWN movement: end is a valley, use ValleysAnalyser
            // Note: We don't have the next movement frames yet, so we use current movement's end
            // This will be refined when the next movement is processed

            // Calculate weights from the coarse analyser for intensity
            var frameCountToKeep = Math.Max(2, (int)Math.Ceiling(afterTransitionFrames.Count * (double)settings.PercentageOfFramesToKeep / 100));
            var weight = 0L;
            var partialWeight = 0L;
            foreach (var frameX in afterTransitionFrames
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
        /// Finds the precise transition boundary by looking at frames from both sides of the transition
        /// </summary>
        private int FindMovementBoundary(
            List<WeightedMotionVectorsFrameEx> beforeTransitionFrames,
            List<WeightedMotionVectorsFrameEx> afterTransitionFrames,
            Func<WeightedMotionVectorsFrameEx, long> transitionWeightSelector,
            GenerateActionsSettings settings)
        {
            // Combine frames from both sides of the transition
            var transitionZoneFrames = new List<WeightedMotionVectorsFrameEx>();

            // Take frames from the END of the previous movement
            var prevFrameCount = Math.Max(1, Math.Min(beforeTransitionFrames.Count / 3,
                (int)(beforeTransitionFrames.Count * settings.PercentageOfFramesToKeep / 100.0)));
            if (beforeTransitionFrames.Count > 0)
            {
                transitionZoneFrames.AddRange(
                    beforeTransitionFrames.Skip(Math.Max(0, beforeTransitionFrames.Count - prevFrameCount)));
            }

            // Take frames from the START of the current movement
            var currFrameCount = Math.Max(1, Math.Min(afterTransitionFrames.Count / 3,
                (int)(afterTransitionFrames.Count * settings.PercentageOfFramesToKeep / 100.0)));
            if (afterTransitionFrames.Count > 0)
            {
                transitionZoneFrames.AddRange(afterTransitionFrames.Take(currFrameCount));
            }

            if (transitionZoneFrames.Count == 0)
            {
                // Fallback
                if (afterTransitionFrames.Count > 0)
                    return FunscriptActionExtended.TimeToAt(afterTransitionFrames.First().Original.FrameTime);
                if (beforeTransitionFrames.Count > 0)
                    return FunscriptActionExtended.TimeToAt(beforeTransitionFrames.Last().Original.FrameTime);
                return 0;
            }

            // Find the frame with the strongest transition signal
            var bestFrame = transitionZoneFrames
                .OrderByDescending(f => Math.Abs(transitionWeightSelector(f)))
                .FirstOrDefault();

            if (bestFrame != null)
            {
                return FunscriptActionExtended.TimeToAt(bestFrame.Original.FrameTime);
            }

            // Fallback to the boundary between the two movements
            return FunscriptActionExtended.TimeToAt(afterTransitionFrames.First().Original.FrameTime);
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