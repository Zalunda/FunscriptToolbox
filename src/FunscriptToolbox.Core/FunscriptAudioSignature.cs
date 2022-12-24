namespace FunscriptToolbox.Core
{
    public class FunscriptAudioSignature
    {
        public FunscriptAudioSignature(int nbSamplesPerSecond, byte[] compressedSamples, string version = "1.0")
        {
            Version = version;
            NbSamplesPerSecond = nbSamplesPerSecond;
            CompressedSamples = compressedSamples;
        }

        public string Version { get; }
        public int NbSamplesPerSecond { get; }
        public byte[] CompressedSamples { get; }
    }
}
