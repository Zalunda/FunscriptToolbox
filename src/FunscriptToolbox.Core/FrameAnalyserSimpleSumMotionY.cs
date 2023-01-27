using System.Collections.Generic;

namespace FunscriptToolbox.Core
{
    public class FrameAnalyserSimpleSumMotionY : FrameAnalyser
    {
        public override string Name { get; }

        private int[] r_indexes;

        public FrameAnalyserSimpleSumMotionY(string name, int nbBlocX, int nbBlocY, int x, int y, int width, int height)
        {
            this.Name = name;

            var list = new List<int>();
            for (int iy = 0; iy < nbBlocY; iy++)
            {
                if (iy >= y && iy < y + height)
                {
                    for (int ix = 0; ix < nbBlocX; ix++)
                    {
                        if (ix >= x && ix < x + width)
                        {
                            list.Add(iy * nbBlocX + ix);
                        }
                    }
                }
            }

            r_indexes = list.ToArray();
        }

        protected override int ComputeFrameValue(MotionVectorsFrame frame)
        {
            // Note: Beaucoup plus rapide en boucle vs using linq
            var total = 0;
            foreach (var index in r_indexes)
            {
                total += (sbyte)frame.MotionsY[index];
            }
            return total;
        }
    }
}
