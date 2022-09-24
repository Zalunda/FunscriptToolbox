using System;
using System.Collections.Generic;

namespace AudioSynchronization
{
    public class SamplesSection
    {
        public SamplesSection(Sample[] completeList, int startIndex, int length)
        {
            m_completeList = completeList;
            StartIndex = startIndex;
            Length = length;
        }

        private Sample[] m_completeList;

        public int StartIndex { get; }

        public int Length { get; }

        public int LastIndex => StartIndex + Length;

        public bool IsIndexValid(int index)
        {
            return index >= 0 && index < Length;
        }

        public Sample this[int index]
        {
            get
            {
                if (StartIndex + index >= 0 && StartIndex + index < m_completeList.Length)
                    return m_completeList[StartIndex + index];
                else
                    throw new Exception("Blah");
            }
        }

        public IEnumerable<Sample> GetItems()
        {
            for (int i = 0; i < this.Length; i++)
                yield return this[i];
        }

        internal SamplesSection GetSection(int index)
        {
            return new SamplesSection(m_completeList, StartIndex + index, this.Length - index);
        }

        internal SamplesSection GetSection(int index, int length)
        {
            return new SamplesSection(m_completeList, StartIndex + index, Math.Min(length, Length - index));
        }

        internal SamplesSection GetOffsetedSection(int offsetA)
        {
            var newSection = new SamplesSection(m_completeList, StartIndex + offsetA, Length);
            return newSection.StartIndex < 0 || newSection.LastIndex > m_completeList.Length
                ? null
                : newSection;
        }

        public override string ToString()
        {
            return $"{StartIndex,6}-{LastIndex,6} [{Length,6}][{TimeSpan.FromSeconds((double)StartIndex / 120)}]";
        }
    }
}
