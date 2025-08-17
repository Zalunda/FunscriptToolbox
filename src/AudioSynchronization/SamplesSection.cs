using System;
using System.Collections.Generic;

namespace AudioSynchronization
{
    public class SamplesSection
    {
        public SamplesSection(Sample[] completeList, int nbSamplesPerSecond, int startIndex, int length)
        {
            r_completeList = completeList;
            r_nbSamplesPerSecond = nbSamplesPerSecond;
            StartIndex = startIndex;
            Length = length;
        }

        private readonly Sample[] r_completeList;
        private readonly int r_nbSamplesPerSecond;

        public int StartIndex { get; }

        public int Length { get; }

        public int MiddleIndex => (this.StartIndex + this.EndIndex) / 2;
        public int EndIndex => StartIndex + Length;


        public Sample this[int index] => r_completeList[StartIndex + index];

        public IEnumerable<Sample> GetItems()
        {
            for (int i = 0; i < this.Length; i++)
                yield return this[i];
        }

        internal SamplesSection GetSection(int index)
        {
            return new SamplesSection(r_completeList, r_nbSamplesPerSecond, StartIndex + index, this.Length - index);
        }

        internal SamplesSection GetSection(int index, int length)
        {
            return new SamplesSection(r_completeList, r_nbSamplesPerSecond, StartIndex + index, Math.Min(length, Length - index));
        }

        internal SamplesSection GetOffsetedSection(int offsetA)
        {
            var newSection = new SamplesSection(r_completeList, r_nbSamplesPerSecond, StartIndex + offsetA, Length);
            return newSection.StartIndex < 0 || newSection.EndIndex > r_completeList.Length
                ? null
                : newSection;
        }

        internal SamplesSection GetSectionExpendedStart(int expend)
        {
            return new SamplesSection(r_completeList, r_nbSamplesPerSecond, this.StartIndex - expend, this.Length + expend);
        }

        internal SamplesSection GetSectionExpendedEnd(int expend)
        {
            return new SamplesSection(r_completeList, r_nbSamplesPerSecond, this.StartIndex, this.Length + expend);
        }

        public override string ToString()
        {
            return $"{StartIndex,6}-{EndIndex,6} [{Length,6}]";
        }
    }
}
