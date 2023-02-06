using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.Core.MotionVectors
{
    public class MotionVectorsFileReader : IDisposable
    {
        public int FormatVersion { get; }
        public TimeSpan VideoDuration { get; }
        public int NbFrames { get; }
        public decimal VideoFramerate { get; }
        public int VideoWidth { get; }
        public int VideoHeight { get; }
        public int BlocSize => 16;
        public int NbBlocX { get; }
        public int NbBlocY { get; }
        public int NbBlocTotalPerFrame { get; }

        private readonly BinaryReader r_reader;
        private readonly long r_maximumMemoryUsage;
        private long r_posAfterHeader;
        private int r_frameSize;
        private List<MotionVectorsFrame> m_framesInMemory;
        private long m_memoryUsed;

        public MotionVectorsFileReader(string filepath, int maximumMemoryUsageInMB = 1000)
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
            r_reader.ReadBytes(24); // Read "forFutureUse" bytes

            r_maximumMemoryUsage = maximumMemoryUsageInMB * 1024 * 1024;
            r_posAfterHeader = r_reader.BaseStream.Position;
            r_frameSize = NbBlocTotalPerFrame * 2 + 8; // 8 = 2 * ReadInt32()
            m_framesInMemory = new List<MotionVectorsFrame>();
            m_memoryUsed = 0;
        }

        public int GetFrameNumberFromTime(TimeSpan time)
        {
            return (int)Math.Round(time.TotalSeconds / (double)(1 / VideoFramerate));
        }

        public IEnumerable<MotionVectorsFrame> ReadFrames(TimeSpan start, TimeSpan end)
        {
            var startFrameNumber = GetFrameNumberFromTime(start);
            var endFrameNumber = GetFrameNumberFromTime(end);
            foreach (var frame in ReadFrames(startFrameNumber))
            {
                if (frame.FrameNumber >= endFrameNumber)
                    yield break;

                yield return frame;
            }
        }

        public IEnumerable<MotionVectorsFrame> ReadFrames(int startingFrameNumber = 0)
        {
            r_reader.BaseStream.Seek(r_posAfterHeader + (long)startingFrameNumber * r_frameSize, SeekOrigin.Begin);

            var currentFrameNumber = startingFrameNumber;
            while (r_reader.BaseStream.Position < r_reader.BaseStream.Length)
            {
                var frameFromMemory = GetFrameFromMemory(currentFrameNumber);
                if (frameFromMemory != null)
                {
                    r_reader.BaseStream.Seek(r_frameSize, SeekOrigin.Current);
                    yield return frameFromMemory;
                }
                else
                {
                    var frameNumberInFile = r_reader.ReadInt32();
                    if (frameNumberInFile != currentFrameNumber)
                    {
                        throw new Exception($"Wrong frame: Received {frameNumberInFile}, Expected {currentFrameNumber}");
                    }
                    var frameTimeInMsInFile = TimeSpan.FromMilliseconds(r_reader.ReadInt32());
                    var frameType = r_reader.ReadChar();
                    r_reader.ReadBytes(11); // Read "forFutureUseBytes" 
                    var motionsX = r_reader.ReadBytes(NbBlocTotalPerFrame);
                    var motionsY = r_reader.ReadBytes(NbBlocTotalPerFrame);
                    var frameFromFile = new MotionVectorsFrame(
                        frameNumberInFile, 
                        frameTimeInMsInFile,
                        frameType,
                        motionsX, 
                        motionsY);

                    if (m_memoryUsed + r_frameSize < r_maximumMemoryUsage)
                    {
                        var lastFrameInMemory = m_framesInMemory.LastOrDefault();
                        if (lastFrameInMemory == null || currentFrameNumber == lastFrameInMemory.FrameNumber + 1)
                        {
                            m_framesInMemory.Add(frameFromFile);
                            m_memoryUsed += r_frameSize;
                        }
                    }

                    yield return frameFromFile;
                }

                currentFrameNumber++;
            }
        }

        private MotionVectorsFrame GetFrameFromMemory(int frameNumber)
        {
            var firstFrameInMemory = m_framesInMemory.FirstOrDefault()?.FrameNumber ?? -1;
            var lastFrameInMemory = m_framesInMemory.LastOrDefault()?.FrameNumber ?? -1;

            return (frameNumber >= firstFrameInMemory) && (frameNumber <= lastFrameInMemory)
                ? m_framesInMemory[frameNumber - firstFrameInMemory]
                : null;
        }

        public void Dispose()
        {
            r_reader.Dispose();
        }
    }
}