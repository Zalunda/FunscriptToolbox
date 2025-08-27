using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class TimingOverlap<T1, T2> where T1 : ITiming where T2 : ITiming
    {
        public static TimingOverlap<T1, T2> CalculateOverlap(T1 a, T2 b)
        {
            // Validate inputs
            if (a.EndTime <= a.StartTime || b.EndTime <= b.StartTime)
                return null;

            // Find overlap start and end
            TimeSpan overlapStart = TimeSpan.FromTicks(Math.Max(a.StartTime.Ticks, b.StartTime.Ticks));
            TimeSpan overlapEnd = TimeSpan.FromTicks(Math.Min(a.EndTime.Ticks, b.EndTime.Ticks));

            // If no overlap
            if (overlapEnd <= overlapStart)
                return null;

            // Calculate durations
            double overlapDuration = (overlapEnd - overlapStart).Ticks;
            double durationA = (a.EndTime - a.StartTime).Ticks;
            double durationB = (b.EndTime - b.StartTime).Ticks;

            return new TimingOverlap<T1, T2>(a, b, (float)(overlapDuration / durationA), (float)(overlapDuration / durationB));
        }

        public T1 A { get; }
        public T2 B { get; }
        public float OverlapA { get; }
        public float OverlapB { get; }

        public TimingOverlap(T1 a, T2 b, float overlapA, float overlapB)
        {
            A = a;
            B = b;
            OverlapA = overlapA;
            OverlapB = overlapB;
        }
    }
}
