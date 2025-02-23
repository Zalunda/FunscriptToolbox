using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.Core.MotionVectors
{
    public class FrameAnalyser
    {
        public MotionVectorsFrameLayout FrameLayout { get; }
        public BlocAnalyserRule[] Rules { get; }
        public int ActivityLevel { get; }
        public int QualityLevel { get; }
        public MotionVectorsFrame<CellMotionInt>[] StoredFrames { get; }
        public int[] TopReferenceFrameIndexes { get; }
        public int[] BottomReferenceFrameIndexes { get; }

        public FrameAnalyser(
            MotionVectorsFrameLayout frameLayout, 
            BlocAnalyserRule[] rules = null,
            MotionVectorsFrame<CellMotionInt>[] storedFrames = null,
            int[] topReferenceFrameIndexes = null,
            int[] bottomReferenceFrameIndexes = null,
            int activityLevel = 0, 
            int qualityLevel = 0)
        {
            this.FrameLayout = frameLayout;
            this.Rules = rules ?? Array.Empty<BlocAnalyserRule>();
            this.StoredFrames = storedFrames;
            this.TopReferenceFrameIndexes = topReferenceFrameIndexes;
            this.BottomReferenceFrameIndexes = bottomReferenceFrameIndexes;
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
                this.StoredFrames, 
                this.TopReferenceFrameIndexes, 
                this.BottomReferenceFrameIndexes,
                activityLevel,
                qualityLevel);
        }

        public FunscriptAction[] CreateActions(
            MotionVectorsFileReader mvsReader, 
            TimeSpan startingTime, 
            TimeSpan endTime, 
            double maximumNbStrokesDetectedPerSecond)
        {
            var actions = new List<FunscriptActionWithWeight>();

            var previousFrameTime = TimeSpan.Zero;
            var currentActionStartTime = TimeSpan.MinValue;
            var currentActionEndTime = TimeSpan.Zero;
            var lastFrameTotalWeight = 0L;
            var minimumActionDuration = TimeSpan.FromMilliseconds(1000.0 / (maximumNbStrokesDetectedPerSecond * 2) - 20);
            var actionsWithSameDirectionAccumulator = new List<long>();

            var weights = new List<long>();
            foreach (var frame in mvsReader.ReadFrames(startingTime, endTime))
            {
                var currentFrameTotalWeight = ComputeFrameTotalWeight(frame);
                weights.Add(currentFrameTotalWeight);
                if (currentActionStartTime < TimeSpan.Zero)
                {
                    currentActionStartTime = frame.FrameTime;
                }
                if (previousFrameTime == TimeSpan.Zero)
                {
                    previousFrameTime = frame.FrameTime;
                }

                if (currentFrameTotalWeight == 0 // i.e. probably one of the rare I-frame
                    || (currentFrameTotalWeight > 0 && lastFrameTotalWeight > 0)
                    || (currentFrameTotalWeight < 0 && lastFrameTotalWeight < 0))
                {
                    // Same direction...
                    actionsWithSameDirectionAccumulator.Add(currentFrameTotalWeight);
                    currentActionEndTime = frame.FrameTime;
                }
                else
                {
                    // Direction has changed...
                    if (currentActionEndTime - currentActionStartTime >= minimumActionDuration)
                    {
                        AddActionWithoutDuplicate(
                            actions, 
                            (int)currentActionStartTime.TotalMilliseconds, 
                            (lastFrameTotalWeight > 0) ? 0 : 100, 
                            actionsWithSameDirectionAccumulator);
                        AddActionWithoutDuplicate(
                            actions, 
                            (int)currentActionEndTime.TotalMilliseconds, 
                            (lastFrameTotalWeight > 0) ? 100 : 0, 
                            actionsWithSameDirectionAccumulator);
                    }

                    lastFrameTotalWeight = currentFrameTotalWeight;
                    currentActionStartTime = previousFrameTime;

                    actionsWithSameDirectionAccumulator.Clear();
                    actionsWithSameDirectionAccumulator.Add(currentFrameTotalWeight);
                }
                previousFrameTime = frame.FrameTime;
            }

            using (var writer = File.CreateText("weights.log"))
            {
                foreach (var weight in weights)
                {
                    writer.WriteLine(weight.ToString());
                }
            }

            // TODO: Find a better way to compute amplitude ???
            var sortedWeight = actions
                .Where(f => f.Pos == 100)
                .Select(f => Math.Abs(f.WeightPerFrame))
                .OrderBy(w => w)
                .ToArray();
            var targetWeigth = (sortedWeight.Length > 0)
                ? sortedWeight[sortedWeight.Length * 9 / 10]
                : 0;
            return actions.Select((action, i) => new FunscriptAction
            {
                At = action.At,
                Pos = (action.Pos == 100)
                    ? (i == 0) 
                        ? 100
                        : Math.Max(1, Math.Min(100, (int)((double)100 * Math.Abs(action.WeightPerFrame) / targetWeigth)))
                    : action.Pos
            }).ToArray();
        }

        private static void AddActionWithoutDuplicate(List<FunscriptActionWithWeight> actions, int at, int pos, List<long> results = null)
        {
            var lastAction = actions.LastOrDefault();
            if (lastAction?.At == at && lastAction?.Pos == pos)
            {
                // Skip because action already exists
            }
            else if (lastAction?.At == at && lastAction?.Pos != pos)
            {
                throw new Exception("What?");
            }
            else
            {
                actions.Add(new FunscriptActionWithWeight(at, pos, results));
            }
        }

        // TODO: See if unsafe code could be faster here
        protected virtual long ComputeFrameTotalWeight(MotionVectorsFrame<CellMotionSByte> frame)
        {
            var lookup = MotionVectorsHelper.GetLookupMotionXYAndDirectionToWeightTable();

            // Note: Much faster to loop than using linq\Sum.
            long total = 0;
            foreach (var rule in this.Rules)
            {
                total += lookup[(byte)frame.Motions[rule.Index].X, (byte)frame.Motions[rule.Index].Y, rule.Direction];
            }
            return total;
        }

        private class FunscriptActionWithWeight : FunscriptAction
        {
            public int NbResults { get; }
            public long TotalWeight { get; }
            public long WeightPerFrame { get; }

            public FunscriptActionWithWeight(int at, int pos, List<long> results)
                : base(at, pos)
            {
                if (results == null)
                {
                    NbResults = 0;
                    TotalWeight = 0;
                    WeightPerFrame = 0;
                }
                else
                {
                    NbResults = results.Count;
                    TotalWeight = results.Sum(f => f);
                    WeightPerFrame = results.Count == 0 ? 0 : TotalWeight / results.Count; // TODO Should I use an average like that?
                }
            }

            public override string ToString()
            {
                return $"{Pos,3}, {NbResults,3}, {TotalWeight,12}, {WeightPerFrame,12}, {At}";
            }
        }
    }
}
