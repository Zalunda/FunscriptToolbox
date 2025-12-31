using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace FunscriptToolbox.Core.MotionVectors
{
    public static class FrameAnalyserGenerator
    {
        public enum FrameBlockType
        {
            ObviousUp,
            ObviousDown,
            Peak,
            Valley
        }

        public sealed class FrameBlock
        {
            public FrameBlockType Type { get; }
            public IReadOnlyList<MotionVectorsFrame<CellMotionSByte>> Frames { get; }
            public MotionVectorsFrame<CellMotionSByte> ReferenceFrame { get; }

            public FrameBlock(
                FrameBlockType type,
                IReadOnlyList<MotionVectorsFrame<CellMotionSByte>> frames,
                MotionVectorsFrame<CellMotionSByte> referenceFrame = null)
            {
                Type = type;
                Frames = frames;
                ReferenceFrame = referenceFrame;
            }
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

        private enum MovementPhase
        {
            Transition,
            Coarse
        }

        private enum MovementDirection
        {
            Up,
            Down
        }

        // ------------------------------------------------------------
        // ENTRY POINT
        // ------------------------------------------------------------

        public static FrameAnalyser CreateFromScriptSequence(
            MotionVectorsFileReader reader,
            FunscriptAction[] actions,
            LearnFromActionsSettings settings)
        {
            var watch = Stopwatch.StartNew();

            var (coarseBlocks, peakBlocks, valleyBlocks) =
                AnalyseReferenceActions(reader, actions, settings);

            var coarseAnalyser = CreateAnalyserFromBlocks(
                coarseBlocks,
                reader.FrameLayout);

            var peaksAnalyser = CreateAnalyserFromBlocks(
                peakBlocks,
                reader.FrameLayout);

            var valleysAnalyser = CreateAnalyserFromBlocks(
                valleyBlocks,
                reader.FrameLayout);

            watch.Stop();

            return new FrameAnalyser(
                reader.FrameLayout,
                coarseAnalyser,
                peaksAnalyser,
                valleysAnalyser);
        }

        // ------------------------------------------------------------
        // STEP 1: SPLIT FRAMES INTO BLOCKS
        // ------------------------------------------------------------

        private static (
            List<FrameBlock> coarse,
            List<FrameBlock> peaks,
            List<FrameBlock> valleys)
        AnalyseReferenceActions(
            MotionVectorsFileReader reader,
            FunscriptAction[] actions,
            LearnFromActionsSettings settings)
        {
            var frameDuration = TimeSpan.FromSeconds(1.0 / (float)reader.VideoFramerate);
            var ignoreFramesDuration = TimeSpan.FromMilliseconds(frameDuration.TotalMilliseconds * (settings.NbFramesToIgnoreAroundAction + 1));

            var availableFrames = reader
                .ReadFrames(
                    actions.First().AtAsTimeSpan - ignoreFramesDuration,
                    actions.Last().AtAsTimeSpan + ignoreFramesDuration)
                .ToList();

            var coarse = new List<FrameBlock>();
            var peaks = new List<FrameBlock>();
            var valleys = new List<FrameBlock>();

            int windowRadius = settings.NbFramesToIgnoreAroundAction;

            for (int i = 0; i < actions.Length; i++)
            {
                int prevDiff = i > 0
                    ? actions[i].Pos - actions[i - 1].Pos
                    : 0;

                int nextDiff = i < actions.Length - 1
                    ? actions[i + 1].Pos - actions[i].Pos
                    : 0;

                FrameBlockType? type = null;

                if (i == 0)
                {
                    // TODO TEST
                    type = (nextDiff > 0) 
                        ? FrameBlockType.Valley
                        : FrameBlockType.Peak;
                }
                else if (i == actions.Length - 1)
                {
                    // TODO TEST
                    type = (nextDiff > 0)
                        ? FrameBlockType.Peak
                        : FrameBlockType.Valley;
                }
                else
                {
                    // TODO TEST
                    if (prevDiff > 0 && nextDiff < 0)
                        type = FrameBlockType.Peak;
                    else if (prevDiff < 0 && nextDiff > 0)
                        type = FrameBlockType.Valley;
                }

                if (type == null)
                    continue;

                if (availableFrames.Count == 0)
                    break;

                var index = FindClosestFrameIndex(
                    availableFrames,
                    actions[i].AtAsTimeSpan);
                var referenceFrame = availableFrames[index];

                int start = Math.Max(0, index - windowRadius);
                int end = Math.Min(availableFrames.Count - 1, index + windowRadius);

                var frames = availableFrames
                    .GetRange(start, end - start + 1);

                if (frames.Count < windowRadius * 2 + 1)
                    continue;

                // Remove used frames to prevent overlap
                availableFrames.RemoveRange(start, end - start + 1);

                var obviousFrames = availableFrames
                        .GetRange(0, start)
                        .ToArray();
                availableFrames.RemoveRange(0, start);
                if (i > 0)
                {
                    coarse.Add(new FrameBlock(type == FrameBlockType.Peak ? FrameBlockType.ObviousUp : FrameBlockType.ObviousDown, obviousFrames));
                }

                var block = new FrameBlock(type.Value, frames, referenceFrame);

                if (type == FrameBlockType.Peak)
                    peaks.Add(block);
                else
                    valleys.Add(block);
            }

            return (coarse, peaks, valleys);
        }

        private static int FindClosestFrameIndex(
            IReadOnlyList<MotionVectorsFrame<CellMotionSByte>> frames,
            TimeSpan targetTime)
        {
            int bestIndex = 0;
            var bestDelta = TimeSpan.MaxValue;

            for (int i = 0; i < frames.Count; i++)
            {
                var delta = (frames[i].FrameTime - targetTime).Duration();
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        // ------------------------------------------------------------
        // STEP 2: LEARN ANALYSER FROM BLOCKS
        // ------------------------------------------------------------
        private static FrameDirectionAnalyser CreateAnalyserFromBlocks(
            IReadOnlyList<FrameBlock> blocks,
            MotionVectorsFrameLayout frameLayout)
        {
            var temp = new TempValue[
                frameLayout.NbCellsTotalPerFrame,
                MotionVectorsHelper.NbBaseDirection / 2];

            var lookup = MotionVectorsHelper.GetLookupMotionXYAndDirectionToWeightTable();

            foreach (var block in blocks)
            {
                var expectedSign = ExpectedDirection(block.Type);
                if (expectedSign == 0)
                    continue;

                foreach (var frame in block.Frames)
                {
                    unsafe
                    {
                        fixed (short* pLookup = lookup)
                        fixed (CellMotionSByte* pMotion = frame.Motions)
                        fixed (TempValue* pTemp = temp)
                        {
                            var pCurrentMotion = pMotion;
                            var pCurrentTemp = pTemp;

                            for (int i = 0; i < frameLayout.NbCellsTotalPerFrame; i++, pCurrentMotion++)
                            {
                                var baseOffset =
                                    ((byte)pCurrentMotion->X * 256 + (byte)pCurrentMotion->Y)
                                    * MotionVectorsHelper.NbBaseDirection;
                                for (int d = 0; d < MotionVectorsHelper.NbBaseDirection / 2; d++, pCurrentTemp++)
                                {
                                    var motionWeight = *(pLookup + baseOffset + d);
                                    var motionSign = Math.Sign(motionWeight);

                                    if (motionSign == expectedSign)
                                    {
                                        pCurrentTemp->NbRightDirection++;
                                        pCurrentTemp->WeightWhenRight += Math.Abs(motionWeight);
                                    }
                                    else if (motionSign == 0)
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
                }
            }

            var rules = new List<BlocDirectionRule>();
            for (int i = 0; i < frameLayout.NbCellsTotalPerFrame; i++)
            {
                TempValue best = default;
                float bestScore = float.MinValue;
                byte bestDirection = 0;

                for (int d = 0; d < MotionVectorsHelper.NbBaseDirection / 2; d++)
                {
                    var current = temp[i, d];
                    var score =
                        1000f * current.WeightWhenRight /
                        (current.WeightWhenRight + current.WeightWhenWrong + 1);

                    if (score > bestScore)
                    {
                        best = current;
                        bestScore = score;
                        bestDirection = (byte)d;
                    }
                }

                var nbFrames =
                    best.NbRightDirection +
                    best.NbNeutralDirection +
                    best.NbWrongDirection;

                var nbDirectional =
                    best.NbRightDirection +
                    best.NbWrongDirection;

                var activity = nbFrames == 0 ? 0 : 100f * nbDirectional / nbFrames;
                var quality = nbDirectional == 0 ? 50 : 100f * best.NbRightDirection / nbDirectional;

                rules.Add(new BlocDirectionRule(
                    (ushort)i,
                    bestDirection,
                    activity,
                    quality,
                    bestScore));
            }

            return new FrameDirectionAnalyser(frameLayout, rules.ToArray());
        }

        private static int ExpectedDirection(FrameBlockType type)
        {
            return type switch
            {
                FrameBlockType.ObviousUp => +1,
                FrameBlockType.ObviousDown => -1,
                FrameBlockType.Valley => +1,
                FrameBlockType.Peak => -1,
                _ => 0
            };
        }
    }
}