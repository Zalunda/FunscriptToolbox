using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.Core
{
    public class FrameAnalyserGeneric : FrameAnalyser
    {
        public override string Name { get; }

        public FrameAnalyserRule[] Rules { get; }

        public FrameAnalyserGeneric(string name, IEnumerable<FrameAnalyserRule> rules)
        {
            this.Name = name;
            this.Rules = rules.ToArray();
        }

        protected override long ComputeFrameValue(MotionVectorsFrame frame)
        {
            // Note: Beaucoup plus rapide en boucle vs using linq
            long total = 0;
            foreach (var rule in this.Rules)
            {
                total += MotionVectorsHelper.GetFinalMotionLength(frame.MotionsX[rule.Index], frame.MotionsY[rule.Index], rule.Direction);
            }
            return total;
        }
    }
}
