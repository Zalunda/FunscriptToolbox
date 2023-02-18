using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.Core.MotionVectors
{

    public class FrameAnalyser
    {
        public int NbBlocX { get; }
        public int NbBlocY { get; }
        public BlocAnalyserRule[] Rules { get; }
        public int ActivityLevel { get; }
        public int QualityLevel { get; }

        public FrameAnalyser(int nbBlocX, int nbBlocY, BlocAnalyserRule[] rules = null, int activityLevel = 0, int qualityLevel = 0)
        {
            this.NbBlocX = nbBlocX;
            this.NbBlocY = nbBlocY;
            this.Rules = rules;
            this.ActivityLevel = activityLevel;
            this.QualityLevel = qualityLevel;
        }

        public FrameAnalyser Filter(int activityLevel, int qualityLevel)
        {
            return new FrameAnalyser(
                this.NbBlocX,
                this.NbBlocY,
                this.Rules
                    .Where(rule => rule.Activity >= activityLevel)
                    .Where(rule => rule.Quality >= qualityLevel)
                    .ToArray(),
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
            var currentActionStartTime = TimeSpan.Zero;
            var currentActionEndTime = TimeSpan.Zero;
            var lastFrameTotalWeight = 0L;
            var minimumActionDuration = TimeSpan.FromMilliseconds(1000.0 / maximumNbStrokesDetectedPerSecond);
            var actionsWithSameDirectionAccumulator = new List<long>();

            foreach (var frame in mvsReader.ReadFrames(startingTime, endTime))
            {
                var currentFrameTotalWeight = ComputeFrameTotalWeight(frame);
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
                        if (lastFrameTotalWeight > 0)
                        {
                            AddActionWithoutDuplicate(actions, (int)currentActionStartTime.TotalMilliseconds, 0);
                            AddActionWithoutDuplicate(actions, (int)currentActionEndTime.TotalMilliseconds, 100, actionsWithSameDirectionAccumulator);
                        }
                        else
                        {
                            AddActionWithoutDuplicate(actions, (int)currentActionStartTime.TotalMilliseconds, 100);
                            AddActionWithoutDuplicate(actions, (int)currentActionEndTime.TotalMilliseconds, 0, actionsWithSameDirectionAccumulator);
                        }
                    }

                    lastFrameTotalWeight = currentFrameTotalWeight;
                    currentActionStartTime = previousFrameTime;

                    actionsWithSameDirectionAccumulator.Clear();
                    actionsWithSameDirectionAccumulator.Add(currentFrameTotalWeight);
                }
                previousFrameTime = frame.FrameTime;
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
                    ? (i == 0) ? 100 : Math.Min(100, (int)((double)100 * Math.Abs(action.WeightPerFrame) / targetWeigth))
                    : action.Pos
            }).ToArray();
        }

        private static void AddActionWithoutDuplicate(List<FunscriptActionWithWeight> actions, int at, int pos, List<long> results = null)
        {
            var lastAction = actions.LastOrDefault();
            if (lastAction?.At == at && lastAction?.Pos == pos)
            {
                // Skip because action already exists
                if (results?.Count > 0)
                {
                    throw new Exception("What?");
                }
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
        protected virtual long ComputeFrameTotalWeight(MotionVectorsFrame frame)
        {
            var lookup = MotionVectorsHelper.GetLookupMotionXYAndDirectionToWeightTable();

            // Note: Much faster to loop than using linq\Sum.
            long total = 0;
            foreach (var rule in this.Rules)
            {
                total += lookup[frame.MotionsX[rule.Index], frame.MotionsY[rule.Index], rule.Direction];
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
                    WeightPerFrame = results.Count == 0 ? 0 : TotalWeight / results.Count;
                }
            }

            public override string ToString()
            {
                return $"{Pos,3}, {NbResults,3}, {TotalWeight,12}, {WeightPerFrame,12}, {At}";
            }
        }
    }
}
