namespace AudioSynchronization
{
    public struct Sample
    {
        public double Value;
        public double DiffFromPrevious;
        public int Index;

        public override string ToString() => $"{Index};{Value};{DiffFromPrevious}";
    }
}
