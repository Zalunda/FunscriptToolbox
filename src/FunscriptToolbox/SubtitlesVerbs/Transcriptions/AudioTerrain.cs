using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public enum BoundaryDirection { Inward, Outward }
    public enum BoundaryPointType { Start, End }

    public class AudioTerrain
    {
        public const int MS_PER_FRAME = 10;
        private const int BIG_BUMPER = 7;
        private static readonly TimeSpan CalibrationPadding = TimeSpan.FromSeconds(2.0);

        private readonly float[] _rms;
        public TimeSpan StartTime { get; }
        public TimeSpan EndTime { get; }
        public float StableLimit { get; }

        public AudioTerrain(
            PcmAudio fullAudio,
            TimeSpan origStart,
            TimeSpan origEnd,
            TimeSpan padStart,
            TimeSpan padEnd)
        {
            StartTime = padStart < TimeSpan.Zero ? TimeSpan.Zero : padStart;
            EndTime = padEnd;

            // 1. CALIBRATION ZONE: Always based on original phrase ± 2s
            // Guarantees speech/silence contrast so StableLimit is constant across iterations
            TimeSpan calibStart = origStart - CalibrationPadding;
            if (calibStart < TimeSpan.Zero) calibStart = TimeSpan.Zero;
            TimeSpan calibEnd = origEnd + CalibrationPadding;

            var calibSnippet = fullAudio.ExtractSnippet(calibStart, calibEnd);
            StableLimit = CalculateDynamicStableLimit(calibSnippet.ToRms(MS_PER_FRAME));

            // 2. NAVIGABLE SANDBOX: Sliced to padStart/padEnd (the physical limits)
            var navigableSnippet = fullAudio.ExtractSnippet(StartTime, EndTime);
            _rms = navigableSnippet.ToRms(MS_PER_FRAME);
        }

        public AudioTimingCursor From(TimeSpan startTime, TimeSpan endTime)
        {
            return new AudioTimingCursor(this, TimeToFrame(startTime), TimeToFrame(endTime));
        }

        internal int FrameCount => _rms.Length;

        internal int TimeToFrame(TimeSpan time)
        {
            int frame = (int)((time - StartTime).TotalMilliseconds / MS_PER_FRAME);
            return Math.Max(0, Math.Min(_rms.Length - 1, frame));
        }

        internal TimeSpan FrameToTime(int frame)
        {
            return StartTime + TimeSpan.FromMilliseconds(frame * MS_PER_FRAME);
        }

        internal float GetRms(int frame) => _rms[frame];

        internal float GetVariance(int left, int right)
        {
            int start = Math.Max(0, left);
            int end = Math.Min(_rms.Length - 1, right);
            if (start > end) return 0f;

            float vMin = float.MaxValue;
            float vMax = float.MinValue;
            for (int k = start; k <= end; k++)
            {
                if (_rms[k] < vMin) vMin = _rms[k];
                if (_rms[k] > vMax) vMax = _rms[k];
            }
            return vMax - vMin;
        }

        private static float CalculateDynamicStableLimit(float[] calibRms)
        {
            float maxMovement = 0;
            float minMovement = float.MaxValue;

            for (int i = 0; i < calibRms.Length; i++)
            {
                int start = Math.Max(0, i - BIG_BUMPER);
                int end = Math.Min(calibRms.Length - 1, i + BIG_BUMPER);
                if (start > end) continue;

                float vMin = float.MaxValue, vMax = float.MinValue;
                for (int k = start; k <= end; k++)
                {
                    if (calibRms[k] < vMin) vMin = calibRms[k];
                    if (calibRms[k] > vMax) vMax = calibRms[k];
                }
                float v = vMax - vMin;

                if (v > maxMovement) maxMovement = v;
                if (v < minMovement) minMovement = v;
            }

            if (minMovement == float.MaxValue) minMovement = 0;
            return Math.Max(minMovement + (maxMovement - minMovement) * 0.08f, 0.002f);
        }
    }

    public class AudioTimingCursor
    {
        private readonly AudioTerrain _terrain;

        public BoundaryEndpoint Start { get; }
        public BoundaryEndpoint End { get; }

        public TimeSpan Duration => End.Position - Start.Position;

        public AudioTimingCursor(AudioTerrain terrain, int startFrame, int endFrame)
        {
            _terrain = terrain;
            Start = new BoundaryEndpoint(terrain, startFrame, BoundaryPointType.Start, parent: this);
            End = new BoundaryEndpoint(terrain, endFrame, BoundaryPointType.End, parent: this);
        }

        public BoundaryEndpoint this[BoundaryPointType pointType] => pointType == BoundaryPointType.Start ? Start : End;

        /// <summary>
        /// Guarantees that End > Start and that the interval meets the minimum duration requirement,
        /// expanding outward symmetrically from the midpoint if necessary.
        /// </summary>
        public void EnforceMinimumDuration(TimeSpan minDuration)
        {
            int minFrames = (int)(minDuration.TotalMilliseconds / AudioTerrain.MS_PER_FRAME);

            if (End.CurrentFrame - Start.CurrentFrame < minFrames)
            {
                int midPoint = (Start.CurrentFrame + End.CurrentFrame) / 2;
                int half = minFrames / 2;

                int bestStart = Math.Max(0, midPoint - half);
                int bestEnd = Math.Min(_terrain.FrameCount - 1, bestStart + minFrames);

                if (bestEnd - bestStart < minFrames)
                {
                    bestStart = Math.Max(0, bestEnd - minFrames);
                }

                Start.SetFrame(bestStart);
                End.SetFrame(bestEnd);
            }
        }
    }

    /// <summary>
    /// Represents a single boundary endpoint that can navigate audio terrain.
    /// </summary>
    public class BoundaryEndpoint
    {
        private const int BIG_BUMPER = 7;   // 70ms
        private const int SMALL_BUMPER = 5; // 50ms

        private readonly AudioTerrain _terrain;
        private readonly AudioTimingCursor _parent;
        private readonly BoundaryPointType _pointType;

        public int CurrentFrame { get; private set; }
        public TimeSpan Position => _terrain.FrameToTime(CurrentFrame);

        public BoundaryEndpoint Opposite => _pointType == BoundaryPointType.Start ? _parent.End : _parent.Start;

        internal BoundaryEndpoint(AudioTerrain terrain, int frame, BoundaryPointType pointType, AudioTimingCursor parent)
        {
            _terrain = terrain;
            _pointType = pointType;
            _parent = parent;
            CurrentFrame = frame;
        }

        internal void SetFrame(int frame) => CurrentFrame = frame;

        private bool IsForwardInTime(BoundaryDirection direction)
        {
            return _pointType == BoundaryPointType.Start
                ? (direction == BoundaryDirection.Inward)
                : (direction == BoundaryDirection.Outward);
        }

        private int GetLimitFrame(bool forward)
        {
            if (forward)
                return _pointType == BoundaryPointType.Start ? Opposite.CurrentFrame : (_terrain.FrameCount - 1);
            else
                return _pointType == BoundaryPointType.End ? Opposite.CurrentFrame : 0;
        }

        public void Step(BoundaryDirection direction, TimeSpan offset)
        {
            int frames = (int)(offset.TotalMilliseconds / AudioTerrain.MS_PER_FRAME);
            bool forward = IsForwardInTime(direction);
            int limit = GetLimitFrame(forward);

            CurrentFrame = forward
                ? Math.Min(limit, CurrentFrame + frames)
                : Math.Max(limit, CurrentFrame - frames);
        }

        public bool MoveIfSilent(BoundaryDirection direction)
        {
            bool forward = IsForwardInTime(direction);
            int limit = GetLimitFrame(forward);

            if (forward)
            {
                bool isSilent = _terrain.GetVariance(CurrentFrame, CurrentFrame + BIG_BUMPER) <= _terrain.StableLimit;
                if (!isSilent) return false;

                CurrentFrame = Math.Min(limit, CurrentFrame + BIG_BUMPER);
                return true;
            }
            else
            {
                bool isSilent = _terrain.GetVariance(CurrentFrame - BIG_BUMPER, CurrentFrame) <= _terrain.StableLimit;
                if (!isSilent) return false;

                CurrentFrame = Math.Max(limit, CurrentFrame - BIG_BUMPER);
                return true;
            }
        }

        public void MoveTowardSpeech(BoundaryDirection direction)
        {
            bool forward = IsForwardInTime(direction);
            int limit = GetLimitFrame(forward);

            if (forward)
            {
                for (int i = CurrentFrame; i < limit; i++)
                {
                    if (_terrain.GetVariance(i - BIG_BUMPER, i + SMALL_BUMPER) > _terrain.StableLimit)
                    {
                        CurrentFrame = i;
                        return;
                    }
                    CurrentFrame = i;
                }
            }
            else
            {
                for (int i = CurrentFrame; i > limit; i--)
                {
                    if (_terrain.GetVariance(i - SMALL_BUMPER, i + BIG_BUMPER) > _terrain.StableLimit)
                    {
                        CurrentFrame = i;
                        return;
                    }
                    CurrentFrame = i;
                }
            }
        }

        /// <summary>
        /// Slides down a speech slope to find the lowest valley or a stable flatline.
        /// Stops immediately if energy starts climbing uphill into a new sound.
        /// </summary>
        public void SlideDownSlopeToStableValley(BoundaryDirection direction, TimeSpan? maxDistance = null)
        {
            // 1. Cap maximum outward wander to ~0.3s (30 frames)
            TimeSpan searchLimit = maxDistance ?? TimeSpan.FromSeconds(0.3);
            int maxSearchFrames = (int)(searchLimit.TotalMilliseconds / AudioTerrain.MS_PER_FRAME);

            float lowestRms = _terrain.GetRms(CurrentFrame);
            int bestFrame = CurrentFrame;
            bool forward = IsForwardInTime(direction);
            int hardLimit = GetLimitFrame(forward);

            // Dynamic threshold: if RMS climbs more than 15% of the dynamic range above the valley,
            // we have definitely hit the next speech mountain.
            float uphillTolerance = Math.Max(_terrain.StableLimit * 0.5f, 0.003f);

            if (!forward)
            {
                int searchEnd = Math.Max(hardLimit, CurrentFrame - maxSearchFrames);

                for (int i = CurrentFrame; i >= searchEnd; i--)
                {
                    float currentRms = _terrain.GetRms(i);

                    // Found a deeper valley
                    if (currentRms < lowestRms)
                    {
                        lowestRms = currentRms;
                        bestFrame = i;
                    }
                    // Uphill detector: we are climbing up a new peak! Stop immediately at the valley.
                    else if (currentRms > lowestRms + uphillTolerance)
                    {
                        break;
                    }

                    // Hit stable silence
                    if (_terrain.GetVariance(i - BIG_BUMPER, i + BIG_BUMPER) <= _terrain.StableLimit)
                    {
                        bestFrame = i;
                        break;
                    }
                }
            }
            else
            {
                int searchEnd = Math.Min(hardLimit, CurrentFrame + maxSearchFrames);

                for (int i = CurrentFrame; i <= searchEnd; i++)
                {
                    float currentRms = _terrain.GetRms(i);

                    // Found a deeper valley
                    if (currentRms < lowestRms)
                    {
                        lowestRms = currentRms;
                        bestFrame = i;
                    }
                    // Uphill detector: we are climbing up into the next word (#26)! Stop immediately.
                    else if (currentRms > lowestRms + uphillTolerance)
                    {
                        break;
                    }

                    // Hit stable silence
                    if (_terrain.GetVariance(i - BIG_BUMPER, i + BIG_BUMPER) <= _terrain.StableLimit)
                    {
                        bestFrame = i;
                        break;
                    }
                }
            }

            CurrentFrame = bestFrame;
        }

        public void SeekStableGround(BoundaryDirection direction)
        {
            bool forward = IsForwardInTime(direction);
            int limit = GetLimitFrame(forward);

            if (!forward)
            {
                for (int i = CurrentFrame; i >= limit; i--)
                {
                    if (_terrain.GetVariance(i - BIG_BUMPER, i + BIG_BUMPER) <= _terrain.StableLimit)
                    {
                        CurrentFrame = i;
                        return;
                    }
                    CurrentFrame = i;
                }
            }
            else
            {
                for (int i = CurrentFrame; i <= limit; i++)
                {
                    if (_terrain.GetVariance(i - BIG_BUMPER, i + BIG_BUMPER) <= _terrain.StableLimit)
                    {
                        CurrentFrame = i;
                        return;
                    }
                    CurrentFrame = i;
                }
            }
        }

        public void SnapToLowestValley(BoundaryDirection direction, TimeSpan radius)
        {
            int radiusFrames = (int)(radius.TotalMilliseconds / AudioTerrain.MS_PER_FRAME);
            bool forward = IsForwardInTime(direction);
            int limit = GetLimitFrame(forward);

            // Fixed: clamped to 'limit' so Inward steps can never cross Opposite!
            int targetFrame = forward
                ? Math.Min(limit, CurrentFrame + radiusFrames)
                : Math.Max(limit, CurrentFrame - radiusFrames);

            int low = Math.Min(CurrentFrame, targetFrame);
            int high = Math.Max(CurrentFrame, targetFrame);

            int minFrame = low;
            float minRms = float.MaxValue;
            for (int i = low; i <= high; i++)
            {
                float rms = _terrain.GetRms(i);
                if (rms < minRms)
                {
                    minRms = rms;
                    minFrame = i;
                }
            }

            CurrentFrame = minFrame;
        }
    }
}