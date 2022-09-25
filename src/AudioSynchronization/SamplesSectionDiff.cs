using System;

namespace AudioSynchronization
{
    public class SamplesSectionDiff
    {
        public SamplesSectionDiff(SamplesSection sectionA, SamplesSection sectionB)
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

        public int Offset => SectionA.StartIndex - SectionB.StartIndex;

        public int Length => SectionA.Length;

        internal void ExpendStart(Sample[] m_completeListA, Sample[] m_completeListB)
        {            
            var expend = Math.Min(
                SectionA.StartIndex, 
                SectionB.StartIndex);
            SectionA = new SamplesSection(m_completeListA, SectionA.StartIndex - expend, SectionA.Length + expend);
            SectionB = new SamplesSection(m_completeListB, SectionB.StartIndex - expend, SectionB.Length + expend);
        }

        internal void ExpendEnd(Sample[] m_completeListA, Sample[] m_completeListB)
        {
            var expend = Math.Min(
                m_completeListA.Length - SectionA.LastIndex, 
                m_completeListB.Length - SectionB.LastIndex);
            SectionA = new SamplesSection(m_completeListA, SectionA.StartIndex, SectionA.Length + expend);
            SectionB = new SamplesSection(m_completeListB, SectionB.StartIndex, SectionB.Length + expend);
        }

        public override string ToString()
        {
            return $"{Offset}, {TotalError,9:0.000}, {SectionA}, {SectionB}";
        }
    }
}