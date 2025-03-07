using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.Core.MotionVectors
{
    public class CreateActionsSettings
    {
        public double MaximumNbStrokesDetectedPerSecond { get; set; }
        public TimeSpan MaximumGapSize { get; set; } = TimeSpan.FromSeconds(0.5);
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

        private class TimeRange
        {
            public TimeSpan Start { get; }
            public TimeSpan End { get; }
            public TimeSpan Middle => TimeSpan.FromMilliseconds((Start.TotalMilliseconds + End.TotalMilliseconds) / 2);

            public TimeRange(TimeSpan start, TimeSpan end)
            {
                this.Start = start;
                this.End = end;
            }

            internal bool IsInRange(TimeSpan time)
            {
                return time >= this.Start && time <= this.End;
            }

            internal TimeSpan ApplyLimit(TimeSpan time)
            {
                return (time < this.Start)
                    ? this.Start
                    : (time > this.End)
                    ? this.End
                    : time;
            }

            internal double GetErrorValue(TimeSpan time)
            {
                var timeMS = time.TotalMilliseconds;
                var startMS = this.Start.TotalMilliseconds;
                var endMS = this.End.TotalMilliseconds;
                return (timeMS < startMS)
                    ? (startMS - timeMS) * 10
                    : (timeMS > endMS)
                    ? (endMS - startMS) * 10
                    : 0;
            }
        }

        private class ActionPoint
        {
            public TimeSpan Time { get; set; }
            public TimeRange TimeRange { get; set; }
            public int Value { get; } // 0 or 100

            public ActionPoint(TimeSpan time, int value)
            {
                Time = time;
                Value = value;
            }

            public double GetErrorValue() => this.TimeRange?.GetErrorValue(this.Time) ?? 0;
        }

        private class ActionHalfStroke
        {
            public ActionPoint Start { get; }
            public ActionPoint End { get; }
            public TimeSpan Duration => this.End.Time - this.Start.Time;
            public FrameWeight[] OriginalFrames { get; }
            public FrameWeight[] FilteredFrames { get; }
            public TimeRange FilteredFramesRange { get; }
            public long TotalWeight { get; }

            public ActionHalfStroke(ActionPoint start, ActionPoint end, IEnumerable<FrameWeight> frames)
            {
                Start = start;
                End = end;
                OriginalFrames = frames.ToArray();
                TotalWeight = OriginalFrames.Sum(of => of.Weight);

                // Sort frames by weight (absolute value, descending)
                // Take the top 70% of frames
                // Remember the minimum and maximum time of those frames
                var frameCountToKeep = (int)Math.Ceiling(OriginalFrames.Length * 0.7);
                var minTime = TimeSpan.MaxValue;
                var maxTime = TimeSpan.MinValue;
                foreach (var frame in frames
                    .OrderByDescending(f => Math.Abs(f.Weight))
                    .Take(frameCountToKeep))
                {
                    var currentFrameTime = frame.Original.FrameTime;
                    if (currentFrameTime < minTime) minTime = currentFrameTime;
                    if (currentFrameTime > maxTime) maxTime = currentFrameTime;
                }

                FilteredFramesRange = new TimeRange(minTime, maxTime);
                FilteredFrames = OriginalFrames
                    .Where(f => FilteredFramesRange.IsInRange(f.Original.FrameTime))
                    .OrderBy(f => f.Original.FrameTime)
                    .ToArray();
            }
        }

        private class ActionFullStroke
        {
            public ActionHalfStroke Up { get; }
            public ActionHalfStroke Down { get; }
            public TimeSpan Duration => this.Up.Duration + this.Down.Duration;
            public int LikelyDuration { get; set; }
            public double UpPercentage => (100.0 * this.Up.Duration.TotalMilliseconds / this.Duration.TotalMilliseconds);

            public double GetErrorValue()
            {
                var error = 0.0;
                error += this.Up.Start.GetErrorValue();
                error += this.Up.End.GetErrorValue(); // Should be the same as this.Start.End
                error += this.Down.End.GetErrorValue();
                var upPerc = this.UpPercentage;
                if (upPerc < 50)
                {
                    error += (50 - upPerc) * 50;
                }
                else if (upPerc > 60)
                {
                    error += (upPerc - 60) * 50;
                }
                return error;
            }

            public ActionFullStroke(ActionHalfStroke up, ActionHalfStroke down)
            {
                Up = up;
                Down = down;
                LikelyDuration = (int)this.Duration.TotalMilliseconds; // Will be updated later
            }
        }

        /// <summary>
        /// Optimizes the timing of action points by detecting and fixing irregularities in stroke durations.
        /// </summary>
        /// <param name="fullstrokes">The list of full strokes to analyze and optimize.</param>
        /// <param name="similarityThresholdPercent">The percentage threshold for determining if durations are similar.</param>
        /// <param name="surroundingStrokesToCheck">The number of surrounding strokes to check for pattern consistency.</param>
        private void OptimizeActionPointTimings(
            List<ActionFullStroke> fullstrokes,
            double similarityThresholdPercent = 20.0,
            int surroundingStrokesToCheck = 1)
        {
            // Need enough strokes to do the comparison
            int minStrokes = 2 * surroundingStrokesToCheck + 2;
            if (fullstrokes.Count < minStrokes)
                return;

            double similarityThreshold = similarityThresholdPercent / 100.0;

            for (int i = surroundingStrokesToCheck; i < fullstrokes.Count - surroundingStrokesToCheck - 1; i++)
            {
                var currentStroke = fullstrokes[i];
                var nextStroke = fullstrokes[i + 1];

                // Check if current pair has inconsistent durations
                //if (IsWithinThreshold(currentStroke.OptimizedDuration.TotalMilliseconds, nextStroke.OptimizedDuration.TotalMilliseconds, similarityThreshold))
                //    continue;

                // Calculate the average duration of the current pair
                double averageDuration = (currentStroke.Duration.TotalMilliseconds + nextStroke.Duration.TotalMilliseconds) / 2.0;

                // Check if surrounding strokes are similar to the average
                bool allSurroundingStrokesSimilar = true;
                for (int j = 1; j <= surroundingStrokesToCheck; j++)
                {
                    var prevStroke = fullstrokes[i - j];
                    var nextPlusStroke = fullstrokes[i + 1 + j];

                    if (!IsWithinThreshold(prevStroke.Duration.TotalMilliseconds, averageDuration, similarityThreshold) ||
                        !IsWithinThreshold(nextPlusStroke.Duration.TotalMilliseconds, averageDuration, similarityThreshold))
                    {
                        allSurroundingStrokesSimilar = false;
                        break;
                    }
                }

                // Find the shared point between current and next strokes
                ActionPoint sharedPoint = currentStroke.Down.End;

                // Verify this is actually the shared point
                if (sharedPoint != nextStroke.Up.Start)
                    continue;

                // If we found a pattern to fix
                if (allSurroundingStrokesSimilar)
                {
                    // Calculate how much we need to adjust
                    double currentDuration = currentStroke.Duration.TotalMilliseconds;
                    double nextDuration = nextStroke.Duration.TotalMilliseconds;
                    double adjustmentMs = (nextDuration - currentDuration) / 2.0;

                    // Apply the adjustment
                    sharedPoint.Time = sharedPoint.TimeRange.ApplyLimit(
                        sharedPoint.Time.Add(TimeSpan.FromMilliseconds(adjustmentMs)));
                }
            }

            var increment = TimeSpan.FromMilliseconds(5);
            int nbUpdates;
            do
            {
                nbUpdates = 0;
                for (int i = 0; i < fullstrokes.Count; i++)
                {
                    var previousStroke = (i > 0) ? fullstrokes[i - 1] : null;
                    var stroke = fullstrokes[i];
                    //var nextStroke = fullstrokes[i + 1];

                    int TryUpdateTiming(TimeSpan increment, Action<TimeSpan> updateTimingAction)
                    {
                        // Include do while if needed

                        var currentError = previousStroke?.GetErrorValue() ?? 0 + stroke.GetErrorValue();
                        updateTimingAction(increment);
                        var newError = previousStroke?.GetErrorValue() ?? 0 + stroke.GetErrorValue();
                        if (newError < currentError)
                        {
                            return 1;
                        }
                        else
                        {
                            updateTimingAction(-increment);
                            return 0;
                        }
                    }

                    nbUpdates += TryUpdateTiming(increment, (inc) =>
                    {
                        stroke.Up.End.Time += inc;
                    });
                    nbUpdates += TryUpdateTiming(increment, (inc) =>
                    {
                        stroke.Up.End.Time -= inc;
                    });
                    nbUpdates += TryUpdateTiming(increment, (inc) =>
                    {
                        stroke.Up.Start.Time += inc;
                        stroke.Up.End.Time += inc;
                    });
                    nbUpdates += TryUpdateTiming(increment, (inc) =>
                    {
                        stroke.Up.Start.Time -= inc;
                        stroke.Up.End.Time -= inc;
                    });
                    nbUpdates += TryUpdateTiming(increment, (inc) =>
                    {
                        stroke.Up.Start.Time += inc;
                    });
                    nbUpdates += TryUpdateTiming(increment, (inc) =>
                    {
                        stroke.Up.Start.Time -= inc;
                    });
                }
            } while (nbUpdates > 0);
        }

        /// <summary>
        /// Determines if two values are within a specified percentage threshold of each other.
        /// </summary>
        private bool IsWithinThreshold(double value1, double value2, double thresholdPercent)
        {
            if (value1 == 0 || value2 == 0)
                return false;

            double diff = Math.Abs(value1 - value2);
            double avg = (value1 + value2) / 2.0;

            return diff <= (avg * thresholdPercent);
        }

        public FunscriptAction[] CreateActions(
            MotionVectorsFileReader mvsReader,
            TimeSpan startingTime,
            TimeSpan endTime,
            CreateActionsSettings settings)
        {
            var points = new List<ActionPoint>();
            var halfstrokes = new List<ActionHalfStroke>();
            var fullstrokes = new List<ActionFullStroke>();

            var previousFrameTime = TimeSpan.Zero;
            var currentActionStartTime = TimeSpan.MinValue;
            var currentActionEndTime = TimeSpan.Zero;
            var lastFrameTotalWeight = 0L;
            var minimumActionDuration = TimeSpan.FromMilliseconds(1000.0 / (settings.MaximumNbStrokesDetectedPerSecond * 2) - 20);
            var framesWithSameDirectionAccumulator = new List<FrameWeight>();

            foreach (var frame in mvsReader.ReadFrames(startingTime, endTime))
            {
                var currentFrameWithWeight = new FrameWeight(frame, ComputeFrameTotalWeight(frame));

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
                        int startValue = (lastFrameTotalWeight > 0) ? 0 : 100;
                        int endValue = (lastFrameTotalWeight > 0) ? 100 : 0;

                        // Create the action points
                        var startPoint = GetOrCreateActionPoint(points, currentActionStartTime, startValue);
                        var endPoint = GetOrCreateActionPoint(points, currentActionEndTime, endValue);

                        // Create a movement connecting these points
                        var movement = new ActionHalfStroke(startPoint, endPoint, framesWithSameDirectionAccumulator);
                        halfstrokes.Add(movement);

                        // Try to pair this movement with the previous one for a full stroke
                        TryCreateFullStroke(halfstrokes, fullstrokes);
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
                int startValue = (lastFrameTotalWeight > 0) ? 0 : 100;
                int endValue = (lastFrameTotalWeight > 0) ? 100 : 0;

                var startPoint = GetOrCreateActionPoint(points, currentActionStartTime, startValue);
                var endPoint = GetOrCreateActionPoint(points, currentActionEndTime, endValue);

                var movement = new ActionHalfStroke(startPoint, endPoint, new List<FrameWeight>(framesWithSameDirectionAccumulator));
                halfstrokes.Add(movement);

                TryCreateFullStroke(halfstrokes, fullstrokes);
            }

            {
                var halfstrokesEnumerator = halfstrokes.GetEnumerator();
                foreach (var point in points)
                {
                    while (halfstrokesEnumerator.Current?.Start != point && halfstrokesEnumerator.Current?.End != point)
                    {
                        halfstrokesEnumerator.MoveNext();
                    }

                    if (halfstrokesEnumerator.Current?.End == point)
                    {
                        var startTimeRange = halfstrokesEnumerator.Current.FilteredFramesRange.End;
                        halfstrokesEnumerator.MoveNext();
                        if (halfstrokesEnumerator.Current?.Start == point)
                        {
                            var endTimeRange = halfstrokesEnumerator.Current.FilteredFramesRange.Start;
                            point.TimeRange = new TimeRange(startTimeRange, endTimeRange);
                        }
                    }
                }
            }

            OptimizeActionPointTimings(fullstrokes, 5, 1);

            using (var writer = File.CreateText("fullstroke.txt"))
            {
                writer.WriteLine($"Time\tLine\tDuration\tOptimizedDuration\tMiddlePoint");
                var line = 1;
                foreach (var fullstroke in fullstrokes)
                {
                    writer.WriteLine($"{fullstroke.Up.Start.Time.TotalMilliseconds}\t{line++}\t{(int)fullstroke.Duration.TotalMilliseconds}\t{fullstroke.Duration.TotalMilliseconds}\t{fullstroke.UpPercentage}");
                }
            }
            using (var writer = File.CreateText("weights.log"))
            {
                var referenceActionsEnumerator = this.ReferenceActions.Select(f => f).GetEnumerator();
                referenceActionsEnumerator.MoveNext();
                var fullstrokesEnumerator = fullstrokes.GetEnumerator();
                fullstrokesEnumerator.MoveNext();
                var halfstrokesEnumerator = halfstrokes.GetEnumerator();
                halfstrokesEnumerator.MoveNext();
                var pointsEnumerator = points.GetEnumerator();
                pointsEnumerator.MoveNext();
                for (double i = startingTime.TotalMilliseconds; i <= endTime.TotalMilliseconds; i++)
                {
                    if (i >= fullstrokesEnumerator.Current?.Up.Start.Time.TotalMilliseconds)
                    {
                        writer.WriteLine($"STROKE [{fullstrokesEnumerator.Current.Duration.TotalMilliseconds:F0},{fullstrokesEnumerator.Current.LikelyDuration}] {fullstrokesEnumerator.Current.Up.Start.Time} => {fullstrokesEnumerator.Current.Down.End.Time} [{fullstrokesEnumerator.Current.UpPercentage}]");
                        fullstrokesEnumerator.MoveNext();
                    }
                    if (i >= halfstrokesEnumerator.Current?.Start.Time.TotalMilliseconds)
                    {
                        writer.WriteLine($"  MOUV [{halfstrokesEnumerator.Current.FilteredFramesRange.Middle.TotalMilliseconds}] {halfstrokesEnumerator.Current.Start.Time} => {halfstrokesEnumerator.Current.End.Time}");
                        halfstrokesEnumerator.MoveNext();
                    }
                    ActionPoint computedPoint = null;
                    if (i >= pointsEnumerator.Current?.Time.TotalMilliseconds)
                    {
                        computedPoint = pointsEnumerator.Current;
                        writer.WriteLine($"    POINT {pointsEnumerator.Current.Time}, {pointsEnumerator.Current.Value}");
                        pointsEnumerator.MoveNext();
                    }
                    if (i >= referenceActionsEnumerator.Current?.At)
                    {
                        if (computedPoint?.TimeRange == null)
                            writer.WriteLine($"    REF-POINT {referenceActionsEnumerator.Current?.AtAsTimeSpan}, {referenceActionsEnumerator.Current?.Pos}");
                        else
                            writer.WriteLine($"    REF-POINT {referenceActionsEnumerator.Current?.AtAsTimeSpan}, {referenceActionsEnumerator.Current?.Pos}");
                        referenceActionsEnumerator.MoveNext();
                    }
                }
            }

            var sortedWeight = halfstrokes
                .Where(hs => hs.End.Value == 100)
                .Select(hs => Math.Abs(hs.TotalWeight))
                .OrderBy(w => w)
                .ToArray();
            var targetWeigth = (sortedWeight.Length > 0)
                ? sortedWeight[sortedWeight.Length * 9 / 10]
                : 0;
            return halfstrokes.Select((hs, i) => new FunscriptAction
            {
                At = (int)hs.Start.Time.TotalMilliseconds,
                Pos = (hs.Start.Value == 100)
                    ? (i == 0) ? 100 : Math.Max(1, Math.Min(100, (int)((double)100 * Math.Abs(hs.TotalWeight) / targetWeigth)))
                    : hs.Start.Value
            }).ToArray();
        }

        private ActionPoint GetOrCreateActionPoint(List<ActionPoint> points, TimeSpan time, int value)
        {
            var previousPoint = points.LastOrDefault();
            if (previousPoint?.Time == time && previousPoint?.Value == value)
            {
                // time - previousPoint.Time < TimeSpan.FromMilliseconds(50) // TODO add to settings
                return previousPoint;
            }

            // Create a new point and add it to the list
            var newPoint = new ActionPoint(time, value);
            points.Add(newPoint);
            return newPoint;
        }

        private void TryCreateFullStroke(List<ActionHalfStroke> movements, List<ActionFullStroke> strokes)
        {
            // We need at least two movements to create a stroke
            if (movements.Count < 2)
                return;

            var lastIndex = movements.Count - 1;
            var current = movements[lastIndex];
            var previous = movements[lastIndex - 1];

            // Check if these movements form a valid stroke (up then down)
            if (previous.Start.Value == 0 && previous.End.Value == 100 &&
                current.Start.Value == 100 && current.End.Value == 0)
            {
                var previousStroke = strokes.LastOrDefault();
                var newStroke = new ActionFullStroke(previous, current);
                strokes.Add(newStroke);
                if (previousStroke != null)
                {
                    previousStroke.LikelyDuration = (int)(newStroke.Up.FilteredFramesRange.Middle - previousStroke.Up.FilteredFramesRange.Middle).TotalMilliseconds;
                }
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

        private class FrameWeight
        {
            public MotionVectorsFrame<CellMotionSByte> Original { get; }
            public long Weight { get; }

            public FrameWeight(MotionVectorsFrame<CellMotionSByte> original, long weight)
            {
                Original = original;
                Weight = weight;
            }
        }

        private class FunscriptActionWithWeight : FunscriptAction
        {
            public int NbResults { get; }
            public List<FrameWeight> Frames { get; }
            public long TotalWeight { get; }
            public TimeSpan StartTime => this.Frames.FirstOrDefault().Original.FrameTime;
            public TimeSpan EndTime => this.Frames.LastOrDefault().Original.FrameTime;

            public FunscriptActionWithWeight(int at, int pos, List<FrameWeight> frames)
                : base(at, pos)
            {
                if (frames == null)
                {
                    NbResults = 0;
                    Frames = new List<FrameWeight>();
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
    }
}
