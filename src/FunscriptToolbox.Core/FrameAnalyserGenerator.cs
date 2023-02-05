using System;
using System.Diagnostics;
using System.Linq;

namespace FunscriptToolbox.Core
{
    public static class FrameAnalyserGenerator
    {
        public static FrameAnalyser CreateFromScriptSequence(
            MotionVectorsFileReader reader,
            FunscriptAction[] actions)
        {
            var watchload = Stopwatch.StartNew();
            var optimized = AnalyseActionsOptimized(reader, actions);
            watchload.Stop();

            // To be able to make sure that the optimized version works as expected
            var validate = false;
            if (validate)
            {
                var clean = AnalyseActionsSlowAndSafe(reader, actions);
                Validate(clean, optimized);
            }

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

        public static unsafe FrameAnalyser AnalyseActionsOptimized(
            MotionVectorsFileReader reader,
            FunscriptAction[] actions)
        {
            var lookupTable = MotionVectorsHelper.GetLookupMotionXYAndAngleToWeightTable();
            var temp = new TempValue[reader.NbBlocTotalPerFrame, MotionVectorsHelper.NbBaseAngles / 2];

            FunscriptAction currentAction = null;
            FunscriptAction nextAction = actions.First();
            var indexAction = 1;
            foreach (var frame in reader.ReadFrames(
                actions.First().AtAsTimeSpan,
                actions.Last().AtAsTimeSpan))
            {
                if (currentAction == null || frame.FrameTimeInMs >= nextAction.AtAsTimeSpan)
                {
                    currentAction = nextAction;
                    nextAction = actions[indexAction++];
                }

                var diff = nextAction.Pos - currentAction.Pos;
                var posOrNegInFunscript = (diff > 0) ? 1 : (diff < 0) ? -1 : 0;

                fixed (short* pLookup = lookupTable)
                fixed (byte* pMotionX = frame.MotionsX)
                fixed (byte* pMotionY = frame.MotionsY)
                fixed (TempValue* pTemp = temp)
                {
                    var pCurrentMotionX = pMotionX;
                    var pCurrentMotionY = pMotionY;
                    var pCurrentTemp = pTemp;

                    for (int i = 0; i < reader.NbBlocTotalPerFrame; i++)
                    {
                        var motionXAsByte = *(pMotionX + i);
                        var motionYAsByte = *(pMotionY + i);
                        for (int k = 0; k < MotionVectorsHelper.NbBaseAngles / 2; k++, pCurrentTemp++)
                        {
                            var motionWeight = *(pLookup + ((motionXAsByte * 256 + motionYAsByte) * MotionVectorsHelper.NbBaseAngles + k));
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
            }

            var rules = new BlocAnalyserRule[reader.NbBlocTotalPerFrame];
            fixed (TempValue* pTemp = temp)
            {
                var pCurrentTemp = pTemp;

                for (int i = 0; i < reader.NbBlocTotalPerFrame; i++)
                {
                    TempValue bestMatchValue = *pCurrentTemp;
                    var bestMatchAngle = 0;
                    for (int j = 0; j < MotionVectorsHelper.NbBaseAngles / 2; j++, pCurrentTemp++)
                    {
                        if ((pCurrentTemp->NbRightDirection > bestMatchValue.NbRightDirection)
                            || (pCurrentTemp->NbRightDirection == bestMatchValue.NbRightDirection && pCurrentTemp->WeightWhenRight > bestMatchValue.WeightWhenRight))
                        {
                            bestMatchValue = *pCurrentTemp;
                            bestMatchAngle = j;
                        }

                        if (pCurrentTemp->NbWrongDirection > bestMatchValue.NbRightDirection || (pCurrentTemp->NbWrongDirection == bestMatchValue.NbRightDirection && pCurrentTemp->WeightWhenWrong > bestMatchValue.WeightWhenRight))
                        {
                            bestMatchValue.NbRightDirection = pCurrentTemp->NbWrongDirection;
                            bestMatchValue.NbWrongDirection = pCurrentTemp->NbRightDirection;
                            bestMatchValue.NbNeutralDirection = pCurrentTemp->NbNeutralDirection;
                            bestMatchValue.WeightWhenRight = pCurrentTemp->WeightWhenWrong;
                            bestMatchValue.WeightWhenWrong = pCurrentTemp->WeightWhenRight;
                            bestMatchValue.WeightWhenNeutral = pCurrentTemp->WeightWhenNeutral;
                            bestMatchAngle = j + 6;
                        }
                    }

                    var nbFrames = bestMatchValue.NbRightDirection + bestMatchValue.NbNeutralDirection + bestMatchValue.NbWrongDirection;
                    var nbDirection = bestMatchValue.NbRightDirection + bestMatchValue.NbWrongDirection;
                    var activity = (float)(100f * nbDirection) / nbFrames;
                    var quality = nbDirection == 0 ? 0 : (float)100f * bestMatchValue.NbRightDirection / nbDirection;
                    var weigth = nbDirection == 0 ? 0 : (float)(bestMatchValue.WeightWhenRight - bestMatchValue.WeightWhenWrong) / nbDirection;
                    rules[i] = new BlocAnalyserRule((ushort)i, (byte)bestMatchAngle, activity, quality, weigth);
                }
            }
            return new FrameAnalyser(rules);
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

        public static FrameAnalyser AnalyseActionsSlowAndSafe(
            MotionVectorsFileReader reader,
            FunscriptAction[] actions)
        {
            var lookupTable = MotionVectorsHelper.GetLookupMotionXYAndAngleToWeightTable();
            var temp = new TempValue[reader.NbBlocTotalPerFrame, MotionVectorsHelper.NbBaseAngles];

            FunscriptAction currentAction = null;
            FunscriptAction nextAction = actions.First();
            var indexAction = 1;
            foreach (var frame in reader.ReadFrames(
                actions.First().AtAsTimeSpan,
                actions.Last().AtAsTimeSpan))
            {
                if (currentAction == null || frame.FrameTimeInMs >= nextAction.AtAsTimeSpan)
                {
                    currentAction = nextAction;
                    nextAction = actions[indexAction++];
                }

                var diff = nextAction.Pos - currentAction.Pos;
                var posOrNegInFunscript = (diff > 0) ? 1 : (diff < 0) ? -1 : 0;

                for (int i = 0; i < reader.NbBlocTotalPerFrame; i++)
                {
                    var motionXAsByte = frame.MotionsX[i];
                    var motionYAsByte = frame.MotionsY[i];
                    for (int k = 0; k < MotionVectorsHelper.NbBaseAngles; k++)
                    {
                        var motionWeight = lookupTable[motionXAsByte, motionYAsByte, k];
                        var posOrNeg = (motionWeight > 0) ? 1 : (motionWeight < 0) ? -1 : 0;

                        if (posOrNegInFunscript == posOrNeg)
                        {
                            temp[i, k].NbRightDirection++;
                            temp[i, k].WeightWhenRight += Math.Abs(motionWeight);
                        }
                        else if (posOrNeg == 0)
                        {
                            temp[i, k].NbNeutralDirection++;
                            temp[i, k].WeightWhenNeutral += Math.Abs(motionWeight);
                        }
                        else
                        {
                            temp[i, k].NbWrongDirection++;
                            temp[i, k].WeightWhenWrong += Math.Abs(motionWeight);
                        }
                    }
                }
            }

            var rules = new BlocAnalyserRule[reader.NbBlocTotalPerFrame];
            for (int i = 0; i < reader.NbBlocTotalPerFrame; i++)
            {
                TempValue bestMatchValue = temp[i, 0];
                var bestMatchAngle = 0;

                // Complicated way to iterate to be consistent with the way the optimized version works (i.e. 0, 6, 1, 7, 2, 8, ...)
                for (int jA = 0; jA < MotionVectorsHelper.NbBaseAngles / 2; jA++)
                {
                    for (int jB = jA; jB < MotionVectorsHelper.NbBaseAngles; jB += MotionVectorsHelper.NbBaseAngles / 2)
                    {
                        if (temp[i, jB].NbRightDirection > bestMatchValue.NbRightDirection || (temp[i, jB].NbRightDirection == bestMatchValue.NbRightDirection && temp[i, jB].WeightWhenRight > bestMatchValue.WeightWhenRight))
                        {
                            bestMatchValue = temp[i, jB];
                            bestMatchAngle = jB;
                        }
                    }
                }

                var nbFrames = bestMatchValue.NbRightDirection + bestMatchValue.NbNeutralDirection + bestMatchValue.NbWrongDirection;
                var nbDirection = bestMatchValue.NbRightDirection + bestMatchValue.NbWrongDirection;
                var activity = (float)(100f * nbDirection) / nbFrames;
                var quality = nbDirection == 0 ? 0 : (float)100f * bestMatchValue.NbRightDirection / nbDirection;
                var weigth = nbDirection == 0 ? 0 : (float)(bestMatchValue.WeightWhenRight - bestMatchValue.WeightWhenWrong) / nbDirection;
                rules[i] = new BlocAnalyserRule((ushort)i, (byte)bestMatchAngle, activity, quality, weigth);
            }
            return new FrameAnalyser(rules);
        }
    }
}
