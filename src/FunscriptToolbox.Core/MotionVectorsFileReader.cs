using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.Core
{
    public class MotionVectorsFileReader : IDisposable
    {
        private readonly BinaryReader r_reader;
        private long r_posAfterHeader;

        public int FormatVersion { get; }
        public TimeSpan VideoDuration { get; }
        public int NbFrames { get; }
        public decimal VideoFramerate { get; }
        public int VideoWidth { get; }
        public int VideoHeight { get; }
        public int NbBlocX { get; }
        public int NbBlocY { get; }
        public int NbBlocTotalPerFrame { get; }

        public MotionVectorsFileReader(string filepath)
        {
            r_reader = new BinaryReader(File.OpenRead(filepath), Encoding.ASCII);

            // Read the headers
            var shouldBeF = r_reader.ReadChar(); // TODO Validate this
            var shouldBeT = r_reader.ReadChar();
            var shouldBeM = r_reader.ReadChar();
            var shouldBeV = r_reader.ReadChar();
            FormatVersion = r_reader.ReadInt32();

            VideoDuration = TimeSpan.FromMilliseconds(r_reader.ReadInt32());
            VideoFramerate = (decimal)r_reader.ReadInt32() / 1000;
            NbFrames = r_reader.ReadInt32();
            VideoWidth = r_reader.ReadInt32();
            VideoHeight = r_reader.ReadInt32();
            NbBlocX = r_reader.ReadInt32();
            NbBlocY = r_reader.ReadInt32();
            NbBlocTotalPerFrame = NbBlocX * NbBlocY;

            r_posAfterHeader = r_reader.BaseStream.Position;
        }

        public IEnumerable<MotionVectorsFrame> ReadFrames()
        {
            r_reader.BaseStream.Seek(r_posAfterHeader, SeekOrigin.Begin);
            var frameNumber = 0;
            while (r_reader.BaseStream.Position < r_reader.BaseStream.Length)
            {
                var frameNumberInFile = r_reader.ReadInt32();
                var frameTimeInMsInFile = TimeSpan.FromMilliseconds(r_reader.ReadInt32());
                var motionsX = r_reader.ReadBytes(NbBlocTotalPerFrame);
                var motionsY = r_reader.ReadBytes(NbBlocTotalPerFrame);
                var total = motionsX.Sum(f => (sbyte)f) + motionsY.Sum(f => (sbyte)f);

                yield return new MotionVectorsFrame(frameNumberInFile, frameTimeInMsInFile, motionsX, motionsY);

                // TODO Unneeded, remove it or use it to validate what's in the file
                var frameTimeInMs = (int)((double)(frameNumber * (1.0 / (double)VideoFramerate)) * 1000);
                frameNumber++;
            }
        }

        public void Dispose()
        {
            r_reader.Dispose();
        }
    }
}