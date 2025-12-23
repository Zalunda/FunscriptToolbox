using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace FunscriptToolbox.Core.MotionVectors
{
    public static class FrameAnalyserGenerator
    {
        public static FrameAnalyser CreateFromScriptSequence(
            MotionVectorsFileReader reader,
            FunscriptAction[] actions,
            LearnFromActionsSettings settings)
        {
            var watchload = Stopwatch.StartNew();
            var collection = AnalyseReferenceActions(reader, actions, settings);
            watchload.Stop();

            return collection;
        }

        public struct TempValue
        {
            public int NbRightDirection;
            public int NbWrongDirection;
            public int NbNeutralDirection;
            public long WeightWhenRight;
            public long WeightWhenWrong;
            public long WeightWhenNeutral;
        }

        /// <summary>
        /// Represents the phase of movement being analyzed
        /// </summary>
        private enum MovementPhase
        {
            /// <summary>Transition zone (direction change)</summary>
            Transition,
            /// <summary>Main/obvious movement zone</summary>
            Obvious
        }

        /// <summary>
        /// Direction of the movement
        /// </summary>
        private enum MovementDirection
        {
            Up,   // pos increasing
            Down  // pos decreasing
        }

        public static unsafe FrameAnalyser AnalyseReferenceActions(
            MotionVectorsFileReader reader,
            FunscriptAction[] referenceActions,
            LearnFromActionsSettings settings)
        {
            var lookupTable = MotionVectorsHelper.GetLookupMotionXYAndDirectionToWeightTable();

            // Three separate temp arrays for the three analysers
            var tempObvious = new TempValue[reader.FrameLayout.NbCellsTotalPerFrame, MotionVectorsHelper.NbBaseDirection / 2];
            var tempUpToDown = new TempValue[reader.FrameLayout.NbCellsTotalPerFrame, MotionVectorsHelper.NbBaseDirection / 2];
            var tempDownToUp = new TempValue[reader.FrameLayout.NbCellsTotalPerFrame, MotionVectorsHelper.NbBaseDirection / 2];

            // Calculate transition zone size (frames to capture around direction changes)
            var transitionFrames = settings.NbFramesToIgnoreAroundAction;

            // We need to look a bit before the first action and after the last action
            // to capture the transition zones at the boundaries
            var frameDuration = TimeSpan.FromSeconds(1.0 / (double)reader.VideoFramerate);
            var extraTime = TimeSpan.FromSeconds((transitionFrames + 1) / (double)reader.VideoFramerate);
            var startTime = referenceActions.First().AtAsTimeSpan - extraTime;
            var endTime = referenceActions.Last().AtAsTimeSpan + extraTime;

            // Clamp to valid range
            if (startTime < TimeSpan.Zero) startTime = TimeSpan.Zero;

            var frameCounter = 0;
            var currentSegmentFrames = 0;
            FunscriptAction previousAction = null;
            FunscriptAction currentAction = null;
            FunscriptAction nextAction = referenceActions.First();
            var indexAction = 1;
            var isBeforeFirstAction = true;

            foreach (var frame in reader.ReadFrames(startTime, endTime))
            {
                // Handle action transitions
                while (nextAction != null && frame.FrameTime >= nextAction.AtAsTimeSpan)
                {
                    previousAction = currentAction;
                    currentAction = nextAction;

                    if (indexAction < referenceActions.Length)
                    {
                        nextAction = referenceActions[indexAction++];
                        // Calculate total frames in this segment
                        currentSegmentFrames = (int)((nextAction.AtAsTimeSpan - currentAction.AtAsTimeSpan).TotalSeconds * (double)reader.VideoFramerate);
                    }
                    else
                    {
                        nextAction = null;
                        currentSegmentFrames = 0;
                    }

                    frameCounter = 0;
                    isBeforeFirstAction = false;
                }

                // Determine what phase we're in and the movement direction
                MovementPhase phase;
                MovementDirection? transitionType = null;
                int posOrNegInFunscript = 0;

                if (isBeforeFirstAction)
                {
                    // Before the first action - this is a transition zone leading into the first action
                    if (referenceActions.Length >= 2)
                    {
                        var firstDiff = referenceActions[1].Pos - referenceActions[0].Pos;
                        if (firstDiff > 0)
                        {
                            // First segment goes UP, so we're at the end of a DOWN movement (valley)
                            phase = MovementPhase.Transition;
                            transitionType = MovementDirection.Down;
                            posOrNegInFunscript = -1; // down movement ending
                        }
                        else if (firstDiff < 0)
                        {
                            // First segment goes DOWN, so we're at the end of an UP movement (peak)
                            phase = MovementPhase.Transition;
                            transitionType = MovementDirection.Up;
                            posOrNegInFunscript = 1; // up movement ending
                        }
                        else
                        {
                            frameCounter++;
                            continue;
                        }
                    }
                    else
                    {
                        frameCounter++;
                        continue;
                    }
                }
                else if (currentAction == null || nextAction == null)
                {
                    // After the last action - this is a transition zone
                    if (previousAction != null && currentAction != null)
                    {
                        var lastDiff = currentAction.Pos - previousAction.Pos;
                        if (lastDiff > 0)
                        {
                            // Last segment went UP, so we're transitioning at a peak
                            phase = MovementPhase.Transition;
                            transitionType = MovementDirection.Up;
                            posOrNegInFunscript = 1;
                        }
                        else if (lastDiff < 0)
                        {
                            // Last segment went DOWN, so we're transitioning at a valley
                            phase = MovementPhase.Transition;
                            transitionType = MovementDirection.Down;
                            posOrNegInFunscript = -1;
                        }
                        else
                        {
                            frameCounter++;
                            continue;
                        }
                    }
                    else
                    {
                        frameCounter++;
                        continue;
                    }
                }
                else
                {
                    var diff = nextAction.Pos - currentAction.Pos;
                    posOrNegInFunscript = Math.Sign(diff);

                    if (posOrNegInFunscript == 0)
                    {
                        frameCounter++;
                        continue;
                    }

                    // Determine if we're in a transition zone or obvious zone
                    var framesFromStart = frameCounter;
                    var framesFromEnd = currentSegmentFrames - frameCounter;

                    if (framesFromStart < transitionFrames)
                    {
                        // Near the start of segment - transition from previous direction
                        phase = MovementPhase.Transition;

                        // Determine what direction we're transitioning FROM
                        if (previousAction != null)
                        {
                            var prevDiff = currentAction.Pos - previousAction.Pos;
                            if (prevDiff > 0)
                            {
                                // Previous was going UP, now transitioning (this is a peak - up to down)
                                transitionType = MovementDirection.Up;
                            }
                            else if (prevDiff < 0)
                            {
                                // Previous was going DOWN, now transitioning (this is a valley - down to up)
                                transitionType = MovementDirection.Down;
                            }
                            else
                            {
                                // Previous had no movement, skip
                                frameCounter++;
                                continue;
                            }
                        }
                        else
                        {
                            // No previous action, treat as transition based on opposite of current direction
                            transitionType = (posOrNegInFunscript > 0) ? MovementDirection.Down : MovementDirection.Up;
                        }
                    }
                    else if (framesFromEnd < transitionFrames)
                    {
                        // Near the end of segment - transition to next direction
                        phase = MovementPhase.Transition;

                        // We're at the end of current movement, so transition type is current direction
                        transitionType = (posOrNegInFunscript > 0) ? MovementDirection.Up : MovementDirection.Down;
                    }
                    else
                    {
                        // Middle of segment - obvious movement
                        phase = MovementPhase.Obvious;
                    }
                }

                // Select the appropriate temp array based on phase
                TempValue[,] targetTemp;
                if (phase == MovementPhase.Obvious)
                {
                    targetTemp = tempObvious;
                }
                else if (transitionType == MovementDirection.Up)
                {
                    targetTemp = tempUpToDown; // At peak, transitioning from UP to DOWN
                }
                else
                {
                    targetTemp = tempDownToUp; // At valley, transitioning from DOWN to UP
                }

                // Process the frame
                fixed (short* pLookup = lookupTable)
                fixed (CellMotionSByte* pMotion = frame.Motions)
                fixed (TempValue* pTemp = targetTemp)
                {
                    var pCurrentMotion = pMotion;
                    var pCurrentTemp = pTemp;

                    for (int i = 0; i < reader.FrameLayout.NbCellsTotalPerFrame; i++, pCurrentMotion++)
                    {
                        var baseOffset = ((byte)pCurrentMotion->X * 256 + (byte)pCurrentMotion->Y) * MotionVectorsHelper.NbBaseDirection;
                        for (int k = 0; k < MotionVectorsHelper.NbBaseDirection / 2; k++, pCurrentTemp++)
                        {
                            var motionWeight = *(pLookup + baseOffset + k);
                            var posOrNeg = (motionWeight > 0) ? 1 : (motionWeight < 0) ? -1 : 0;
                            if (posOrNegInFunscript == posOrNeg)
                            {
                                pCurrentTemp->NbRightDirection++;
                                pCurrentTemp->WeightWhenRight += Math.Abs(motionWeight);
                            }
                            else if (posOrNeg == 0)
                            {
                                pCurrentTemp->NbNeutralDirection++;
                                pCurrentTemp->WeightWhenNeutral += Math.Abs(motionWeight);
                            }
                            else
                            {
                                pCurrentTemp->NbWrongDirection++;
                                pCurrentTemp->WeightWhenWrong += Math.Abs(motionWeight);
                            }
                        }
                    }
                }
                frameCounter++;
            }

            // Create the three analysers
            var obviousAnalyser = CreateAnalyserFromTemp(tempObvious, reader.FrameLayout, referenceActions);
            var upToDownAnalyser = CreateAnalyserFromTemp(tempUpToDown, reader.FrameLayout, referenceActions);
            var downToUpAnalyser = CreateAnalyserFromTemp(tempDownToUp, reader.FrameLayout, referenceActions);

            return new FrameAnalyser(
                reader.FrameLayout,
                obviousAnalyser,
                upToDownAnalyser,
                downToUpAnalyser);
        }

        private static FrameAnalyserUnit CreateAnalyserFromTemp(
            TempValue[,] temp,
            MotionVectorsFrameLayout frameLayout,
            FunscriptAction[] referenceActions)
        {
            var nbCells = temp.GetLength(0);
            var nbDirections = temp.GetLength(1);
            var rules = new List<BlocAnalyserRule>();

            for (int i = 0; i < nbCells; i++)
            {
                TempValue best = new TempValue();
                var bestScore = float.MinValue;
                var bestDirection = (byte)0;

                // Find best direction for this cell
                for (int j = 0; j < nbDirections; j++)
                {
                    var current = temp[i, j];
                    var score = 1000 * current.WeightWhenRight / (current.WeightWhenRight + current.WeightWhenWrong + 1);

                    if (score > bestScore)
                    {
                        best = current;
                        bestScore = score;
                        bestDirection = (byte)j;
                    }
                }

                var nbFrames = best.NbRightDirection + best.NbNeutralDirection + best.NbWrongDirection;
                var nbDirection = best.NbRightDirection + best.NbWrongDirection;
                var activity = nbFrames == 0 ? 0 : (float)(100f * nbDirection) / nbFrames;
                var quality = nbDirection == 0 ? 50 : (float)100f * best.NbRightDirection / nbDirection;

                rules.Add(new BlocAnalyserRule(
                    (ushort)i,
                    bestDirection,
                    activity,
                    quality,
                    bestScore
                ));
            }

            return new FrameAnalyserUnit(
                frameLayout,
                rules.ToArray());
        }
    }
}