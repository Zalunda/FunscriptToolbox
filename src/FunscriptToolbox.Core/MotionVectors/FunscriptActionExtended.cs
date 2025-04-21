using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.Core.MotionVectors
{
    public class FunscriptActionExtended : FunscriptAction
    {
        public int AtMin { get; private set; }
        public int AtMax { get; private set; }
        [JsonIgnore]
        public long Weight { get; private set; }

        public FunscriptActionExtended(int at, int pos)
            : base(at, pos)
        {
            AtMin = At;
            AtMax = At;
            Weight = 0;
        }

        internal void SetAtMinAndWeight(int atMin, long weight)
        {
            AtMin = Math.Min(At, atMin);
            Weight = weight;
        }

        internal void SetAtMax(int atMax)
        {
            AtMax = Math.Max(At, atMax);
        }

        internal static int TimeToAt(TimeSpan time)
        {
            return (int)Math.Round(time.TotalMilliseconds);
        }

        public override string ToString()
        {
            return $"{At}, {Pos}, {At - AtMin}, {AtMax - At}, {Weight}";
        }
    }
}
