using System;

namespace AudioSynchronization
{
    public class SamplesSectionMatch
    {
        public SamplesSectionMatch(SamplesSection sectionA, SamplesSection sectionB)
        {
            if (sectionA.Length != sectionB.Length)
                throw new Exception("Length mismatch");

            SectionA = sectionA;
            SectionB = sectionB;

            TotalError = 0.0;
            for (int i = 0; i < sectionA.Length; i++)
            {
                TotalError += Math.Abs(sectionA[i].Value - sectionB[i].Value);
            }
        }

        public SamplesSection SectionA { get; private set; }
        public SamplesSection SectionB { get; private set; }
        public double TotalError { get; }

        public int Length => SectionA.Length;
        public int Offset => SectionB.StartIndex - SectionA.StartIndex;

        internal void ExpendStart()
        {            
            var expend = Math.Min(
                SectionA.StartIndex, 
                SectionB.StartIndex);
            SectionA = this.SectionA.GetSectionExpendedStart(expend);
            SectionB = this.SectionB.GetSectionExpendedStart(expend);
        }

        internal void ExpendEnd(Sample[] m_completeListA, Sample[] m_completeListB)
        {
            var expend = Math.Min(
                m_completeListA.Length - SectionA.EndIndex, 
                m_completeListB.Length - SectionB.EndIndex);
                        
            SectionA = this.SectionA.GetSectionExpendedEnd(expend);
            SectionB = this.SectionB.GetSectionExpendedEnd(expend);
        }

        public override string ToString()
        {
            return $"{Offset,6:0}, {TotalError,9:0}, {SectionA}, {SectionB}";
        }
    }
}