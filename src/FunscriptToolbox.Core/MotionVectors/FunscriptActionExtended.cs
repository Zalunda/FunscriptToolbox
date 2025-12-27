using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.Core.MotionVectors
{
    public class FunscriptActionExtended : FunscriptAction
    {
        [JsonIgnore]
        public long Weight { get; private set; }
        [JsonIgnore]
        public long PartialWeight { get; private set; }

        public FunscriptActionExtended(int at, int pos)
            : base(at, pos)
        {
            Weight = 0;
            PartialWeight = 0;
        }

        internal void SetWeight(long weight, long partialWeight)
        {
            Weight = weight;
            PartialWeight = partialWeight;
        }

        internal static int TimeToAt(TimeSpan time)
        {
            return (int)Math.Round(time.TotalMilliseconds);
        }

        public override string ToString()
        {
            return $"{At}, {Pos}, {Weight}, {PartialWeight}";
        }
    }
}
