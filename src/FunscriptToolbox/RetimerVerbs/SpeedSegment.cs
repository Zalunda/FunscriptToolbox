using System;

namespace FunscriptToolbox.RetimerVerbs
{
    public class SpeedSegment
    {
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public double Speed { get; set; }

        public TimeSpan Duration => this.EndTime - this.StartTime;
        public TimeSpan EstimatedFinalDuration => Speed > 0
            ? TimeSpan.FromMilliseconds(this.Duration.TotalMilliseconds / this.Speed)
            : this.Duration;

        public override string ToString()
        {
            return $"Source: [{StartTime:g}-{EndTime:g}], Speed: {Speed}";
        }

        public void AdjustTime(double videoFramerate)
        {
            var x = 1.0 / videoFramerate;
            var frameStartTime = Math.Ceiling(this.StartTime.TotalSeconds / x);
            this.StartTime = TimeSpan.FromSeconds(frameStartTime * x);
            var frameEndTime = Math.Ceiling(this.EndTime.TotalSeconds / x);
            this.EndTime = TimeSpan.FromSeconds(frameEndTime * x);
        }
    }
}