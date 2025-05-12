using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.Core.MotionVectors
{
    public class CreateActionsSettings
    {
        public double MaximumStrokesDetectedPerSecond { get; set; }
        public TimeSpan MaximumGapSize { get; set; } = TimeSpan.FromSeconds(0.5);
    }

    public class FunscriptActionWithWeight : FunscriptAction
    {
        public int NbResults { get; }
        public List<WeightedMotionVectorsFrame> Frames { get; }
        public long TotalWeight { get; }
        public TimeSpan StartTime => this.Frames.FirstOrDefault().Original.FrameTime;
        public TimeSpan EndTime => this.Frames.LastOrDefault().Original.FrameTime;

        public FunscriptActionWithWeight(int at, int pos, List<WeightedMotionVectorsFrame> frames)
            : base(at, pos)
        {
            if (frames == null)
            {
                NbResults = 0;
                Frames = new List<WeightedMotionVectorsFrame>();
                TotalWeight = 0;
            }
            else
            {
                Frames = frames;
                NbResults = frames.Count;
                TotalWeight = frames.Sum(f => f.Weight);
            }
        }

        public override string ToString()
        {
            return $"{Pos,3}, {NbResults,3}, {TotalWeight,12}, {At}";
        }
    }

    public class FrameAnalyser
    {
        public MotionVectorsFrameLayout FrameLayout { get; }
        public BlocAnalyserRule[] Rules { get; }
        public FunscriptAction[] ReferenceActions { get; }
        public int ActivityLevel { get; }
        public int QualityLevel { get; }

        public FrameAnalyser(
            MotionVectorsFrameLayout frameLayout, 
            BlocAnalyserRule[] rules = null,
            FunscriptAction[] referenceActions = null,
            int activityLevel = 0, 
            int qualityLevel = 0)
        {
            this.FrameLayout = frameLayout;
            this.Rules = rules ?? Array.Empty<BlocAnalyserRule>();
            this.ReferenceActions = referenceActions ?? Array.Empty<FunscriptAction>();
            this.ActivityLevel = activityLevel;
            this.QualityLevel = qualityLevel;
        }

        public FrameAnalyser Filter(int activityLevel, int qualityLevel, double minPercentage)
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
            return new FrameAnalyser(
                this.FrameLayout,
                rules,
                this.ReferenceActions,
                activityLevel,
                qualityLevel);
        }

        public FunscriptActionExtended[] CreateActions(
            MotionVectorsFileReader mvsReader,
            TimeSpan startingTime,
            TimeSpan endTime,
            CreateActionsSettings settings)
        {
            var points = new List<FunscriptActionExtended>();

            var previousFrameTime = TimeSpan.Zero;
            var currentActionStartTime = TimeSpan.MinValue;
            var currentActionEndTime = TimeSpan.Zero;
            var lastFrameTotalWeight = 0L;
            var minimumActionDuration = TimeSpan.FromMilliseconds(1000.0 / (settings.MaximumStrokesDetectedPerSecond * 2) - 20);
            var framesWithSameDirectionAccumulator = new List<WeightedMotionVectorsFrame>();

            foreach (var frame in mvsReader.ReadFrames(startingTime, endTime))
            {
                var totalWeight = ComputeFrameTotalWeight(frame, out var partialWeight);
                var currentFrameWithWeight = new WeightedMotionVectorsFrame(frame, totalWeight, partialWeight);

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
                            framesWithSameDirectionAccumulator);
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
                    framesWithSameDirectionAccumulator);
            }

            var sortedWeight = points
                .Where(point => point.Pos == 100)
                .Select(point => Math.Abs(point.PartialWeight))
                .OrderBy(w => w)
                .ToArray();
            var targetWeigth = (sortedWeight.Length > 0)
                ? sortedWeight[sortedWeight.Length * 9 / 10]
                : 0;
            foreach (var point in points
                .Where(point => point.Pos == 100))
            {
                point.Pos = Math.Max(1, Math.Min(100,
                    (int)((double)100 * Math.Abs(point.Weight) / targetWeigth)));
            }

            return points.ToArray();
        }

        private void AddPoints(
            List<FunscriptActionExtended> points, 
            TimeSpan currentActionStartTime, 
            TimeSpan currentActionEndTime, 
            long lastFrameTotalWeight, 
            List<WeightedMotionVectorsFrame> framesWithSameDirectionAccumulator)
        {
            int startValue = (lastFrameTotalWeight > 0) ? 0 : 100;
            int endValue = (lastFrameTotalWeight > 0) ? 100 : 0;

            // Create the action points
            var startPoint = GetOrAddPoint(points, FunscriptActionExtended.TimeToAt(currentActionStartTime), startValue);
            var endPoint = GetOrAddPoint(points, FunscriptActionExtended.TimeToAt(currentActionEndTime), endValue);

            // Sort frames by weight (absolute value, descending)
            // Take the top 70% of frames
            // Remember the minimum and maximum time of those frames
            var frameCountToKeep = (int)Math.Ceiling(framesWithSameDirectionAccumulator.Count * 0.7);
            var mouvMin = int.MaxValue;
            var mouvMax = int.MinValue;
            var weight = 0L;
            var partialWeight = 0L;
            foreach (var frameX in framesWithSameDirectionAccumulator
                .OrderByDescending(f => Math.Abs(f.Weight))
                .Take(frameCountToKeep))
            {
                var currentFrameAt = FunscriptActionExtended.TimeToAt(frameX.Original.FrameTime);
                if (currentFrameAt < mouvMin) mouvMin = currentFrameAt;
                if (currentFrameAt > mouvMax) mouvMax = currentFrameAt;
                weight += frameX.Weight;
                partialWeight += frameX.PartialWeight;
            }

            startPoint.SetAtMax(mouvMin);
            endPoint.SetAtMinAndWeight(mouvMax, weight, partialWeight);
        }

        private FunscriptActionExtended GetOrAddPoint(List<FunscriptActionExtended> points, int at, int pos)
        {
            var previousPoint = points.LastOrDefault();
            if (previousPoint?.At == at && previousPoint?.Pos == pos)
            {
                // time - previousPoint.Time < TimeSpan.FromMilliseconds(50) // TODO add to settings
                return previousPoint;
            }

            // Create a new point and add it to the list
            var newPoint = new FunscriptActionExtended(at, pos);
            points.Add(newPoint);
            return newPoint;
        }

        // TODO: See if unsafe code could be faster here
        protected virtual long ComputeFrameTotalWeight(MotionVectorsFrame<CellMotionSByte> frame, out long partialWeight)
        {
            var lookup = MotionVectorsHelper.GetLookupMotionXYAndDirectionToWeightTable();

            // Note: Much faster to loop than using linq\Sum.
            long totalWeight = 0;
            partialWeight = 0;
            var partialWeights = new short[this.Rules.Length];
            var i = 0;
            foreach (var rule in this.Rules)
            {
                var weight = lookup[(byte)frame.Motions[rule.Index].X, (byte)frame.Motions[rule.Index].Y, rule.Direction];
                partialWeights[i++] = weight;
                totalWeight += weight;
            }
            // TODO? How do I use that? Highest 100? Or percentage??
            Array.Sort(partialWeights);
            var index0 = Array.BinarySearch(partialWeights, (short)0);
            if (index0 < 0)
                index0 = -(index0 + 1);
            if (totalWeight > 0)
            {
                for (int k = index0; k < partialWeights.Length; k++)
                {
                    partialWeight += partialWeights[k];
                }
            }
            else
            {
                for (int k = index0; k >= 0; k--)
                {
                    partialWeight -= partialWeights[k];
                }
            }
            return totalWeight;
        }
    }
}
