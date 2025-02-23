﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace FunscriptToolbox.Core.MotionVectors
{
    public static class FrameAnalyserGenerator
    {
        public static FrameAnalyser CreateFromScriptSequence(
            MotionVectorsFileReader reader,
            FunscriptAction[] actions)
        {
            var watchload = Stopwatch.StartNew();
            var optimized = AnalyseActions(reader, actions);
            watchload.Stop();

            return optimized;
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

        public static unsafe FrameAnalyser AnalyseActions(
            MotionVectorsFileReader reader,
            FunscriptAction[] actions,
            int framesToIgnoreAroundAction = 3)
        {
            var frameCounter = 0;
            var currentSegmentFrames = 0;
            var lookupTable = MotionVectorsHelper.GetLookupMotionXYAndDirectionToWeightTable();
            var temp = new TempValue[reader.FrameLayout.NbCellsTotalPerFrame, MotionVectorsHelper.NbBaseDirection / 2];

            var storedFrames = new List<MotionVectorsFrame<CellMotionInt>>();
            var topPointIndexes = new List<int>();
            var bottomPointIndexes = new List<int>();

            FunscriptAction currentAction = null;
            FunscriptAction nextAction = actions.First();
            var indexAction = 1;
            foreach (var frame in reader.ReadFrames(
                actions.First().AtAsTimeSpan,
                actions.Last().AtAsTimeSpan))
            {
                storedFrames.Add(frame.Simplify(frame.Layout.CellWidth * 4, frame.Layout.CellHeight * 4));

                // Handle action transition
                if ((currentAction == null || frame.FrameTime >= nextAction.AtAsTimeSpan) && indexAction < actions.Length)
                {
                    currentAction = nextAction;
                    nextAction = actions[indexAction++];
                    if (currentAction.Pos > nextAction.Pos)
                    {
                        topPointIndexes.Add(storedFrames.Count - 1);
                    }
                    else
                    {
                        bottomPointIndexes.Add(storedFrames.Count - 1);
                    }
                    frameCounter = 0;
                    // Calculate total frames in this segment
                    currentSegmentFrames = (int)((nextAction.AtAsTimeSpan - currentAction.AtAsTimeSpan).TotalSeconds * (double)reader.VideoFramerate);
                }

                // Skip frames at start and end of segments
                if (frameCounter < framesToIgnoreAroundAction || frameCounter >= (currentSegmentFrames - framesToIgnoreAroundAction))
                {
                    frameCounter++;
                    continue;
                }

                var diff = nextAction.Pos - currentAction.Pos;
                var posOrNegInFunscript = Math.Sign(diff);

                fixed (short* pLookup = lookupTable)
                fixed (CellMotionSByte* pMotion = frame.Motions)
                fixed (TempValue* pTemp = temp)
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

            // Create final rules
            var rules = new List<BlocAnalyserRule>();
            for (int i = 0; i < reader.FrameLayout.NbCellsTotalPerFrame; i++)
            {
                TempValue best = new TempValue();
                var bestScore = float.MinValue;
                var bestDirection = (byte)0;

                // Find best direction for this cell
                for (int j = 0; j < MotionVectorsHelper.NbBaseDirection / 2; j++)
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
                var activity = (float)(100f * nbDirection) / nbFrames;
                var quality = nbDirection == 0 ? 50 : (float)100f * best.NbRightDirection / nbDirection;
                rules.Add(new BlocAnalyserRule(
                    (ushort)i,
                    bestDirection,
                    activity,
                    quality,
                    bestScore
                ));
            }

            return new FrameAnalyser(
                reader.FrameLayout,
                rules.ToArray(),
                storedFrames.ToArray(),
                topPointIndexes.ToArray(),
                bottomPointIndexes.ToArray());
        }

        private static void Validate(FrameAnalyser clean, FrameAnalyser optimized)
        {
            for (int i = 0; i < clean.Rules.Length; i++)
            {
                var ruleClean = clean.Rules[i];
                var ruleOptimized = optimized.Rules[i];
                if (ruleClean.Index != ruleOptimized.Index
                    || ruleClean.Direction != ruleOptimized.Direction
                    || ruleClean.Activity != ruleOptimized.Activity
                    || ruleClean.Quality != ruleOptimized.Quality
                    || ruleClean.WeightTraveled != ruleOptimized.WeightTraveled)
                {
                    throw new Exception("Validation Error");
                }
            }
        }
    }
}
