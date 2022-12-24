using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace AudioSynchronization
{
    public class AudioSignature
    {
        public static AudioSignature FromSamples(int nbSamplesPerSecond, ushort[] samples)
        {
            var audioSignature = new AudioSignature(nbSamplesPerSecond, CompressSamples(samples));

            // Validation
            var uncompressedSamples = audioSignature.GetUncompressedSamples();
            var validationErrors = 0;
            if (uncompressedSamples.Length != samples.Length)
            {
                throw new Exception("Compress/Decompress: Size mismatch");
            }
            for (int i = 0; i < uncompressedSamples.Length; i++)
            {
                if (uncompressedSamples[i] != samples[i])
                {
                    validationErrors++;
                }
            }
            if (validationErrors > 0)
            {
                throw new Exception($"Compress/Decompress: {validationErrors} validation errors ");
            }

            return audioSignature;
        }

        public AudioSignature(int nbSamplesPerSecond, byte[] compressedSamples)
        {
            NbSamplesPerSecond = nbSamplesPerSecond;
            CompressedSamples = compressedSamples;
        }

        public int NbSamplesPerSecond { get; }
        public byte[] CompressedSamples { get; }

        public ushort[] GetUncompressedSamples()
        {
            var samples = new List<ushort>();
            var compressedStream = new MemoryStream(CompressedSamples);
            using (var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress))
            {
                int firstByte;
                while ((firstByte = gzipStream.ReadByte()) >= 0)
                {
                    var a = (byte)firstByte;
                    var b = (byte)gzipStream.ReadByte();
                    samples.Add((ushort)(a << 8 | b));
                }
            }
            return samples.ToArray();
        }

        private static byte[] CompressSamples(IEnumerable<ushort> samples)
        {
            var memoryStream = new MemoryStream();
            using (var compressedStream = new GZipStream(memoryStream, CompressionLevel.Optimal))
            {
                foreach (var sample in samples)
                {
                    compressedStream.WriteByte((byte)(sample >> 8));
                    compressedStream.WriteByte((byte)(sample & 0xFF));
                }
            }

            return memoryStream.ToArray();
        }
    }
}
