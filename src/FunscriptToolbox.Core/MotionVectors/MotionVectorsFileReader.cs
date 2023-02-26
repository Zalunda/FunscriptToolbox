using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.Core.MotionVectors
{
    public class MotionVectorsFileReader : IDisposable
    {
        public string FilePath { get; }
        public int MaximumMemoryUsageInMB { get; }
        public int FormatVersion { get; }
        public TimeSpan VideoDuration { get; }
        public double FrameDurationInMs => 1000.0 / (double)VideoFramerate;
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
        private readonly Dictionary<int, MotionVectorsFrameWithLastUsedTime> r_framesInMemory;
        private long m_memoryUsed;
        private List<MotionVectorsFrameWithLastUsedTime> m_framesThatCanBeDelete;

        public MotionVectorsFileReader(string filepath, int maximumMemoryUsageInMB = 1000)
        {
            FilePath = filepath;
            MaximumMemoryUsageInMB = maximumMemoryUsageInMB;
            r_reader = new BinaryReader(File.OpenRead(filepath), Encoding.ASCII);

            // Read the headers
            var shouldBeFTMV = Encoding.ASCII.GetString(r_reader.ReadBytes(4));
            if (shouldBeFTMV != "FTMV")
            {
                throw new Exception($"Trying to open .mvs file '{filepath}' but it's not in .mvs format.");
            }
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
            r_frameSize = NbBlocTotalPerFrame * 2 + 20; // 20 => Taille du headers (int=4, int=4, byte=1, future_use=11)
            r_framesInMemory = new Dictionary<int, MotionVectorsFrameWithLastUsedTime>();
            m_memoryUsed = 0;
        }

        private class MotionVectorsFrameWithLastUsedTime
        {
            public MotionVectorsFrameWithLastUsedTime(DateTime dateTimeForFrameInMemory, MotionVectorsFrame frameFromFile)
            {
                LastTimeRead = dateTimeForFrameInMemory;
                Frame = frameFromFile;
            }

            public DateTime LastTimeRead { get; set; }
            public MotionVectorsFrame Frame { get; }
        }

        public int GetFrameNumberFromTime(TimeSpan time)
        {
            return (int)Math.Round(time.TotalSeconds / (double)(1 / VideoFramerate));
        }

        public IEnumerable<MotionVectorsFrame> ReadFrames(TimeSpan start, TimeSpan end)
        {
            m_framesThatCanBeDelete = null;
            var dateTimeForFrameInMemory = DateTime.Now;

            var startFrameNumber = GetFrameNumberFromTime(start);
            var endFrameNumber = GetFrameNumberFromTime(end);
            foreach (var frame in ReadFrames(startFrameNumber, dateTimeForFrameInMemory)) 
            {
                if (frame.FrameNumber >= endFrameNumber)
                    yield break;

                yield return frame;
            }
        }

        public IEnumerable<MotionVectorsFrame> ReadFrames(int startingFrameNumber = 0, DateTime dateTimeForFrameInMemory = default)
        {
            r_reader.BaseStream.Seek(r_posAfterHeader + (long)startingFrameNumber * r_frameSize, SeekOrigin.Begin);

            var currentFrameNumber = startingFrameNumber;
            while (r_reader.BaseStream.Position < r_reader.BaseStream.Length)
            {
                var frameFromMemory = GetFrameFromMemory(currentFrameNumber, dateTimeForFrameInMemory);
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

                    TryAddFrameToMemory(frameFromFile, dateTimeForFrameInMemory);
                    yield return frameFromFile;
                }

                currentFrameNumber++;
            }
        }

        private void TryAddFrameToMemory(MotionVectorsFrame frameFromFile, DateTime dateTimeForFrameInMemory)
        {
            if (m_memoryUsed + r_frameSize < r_maximumMemoryUsage)
            {
                r_framesInMemory.Add(
                    frameFromFile.FrameNumber,
                    new MotionVectorsFrameWithLastUsedTime(dateTimeForFrameInMemory, frameFromFile));
                m_memoryUsed += r_frameSize;
                return;
            }
                        
            if (m_framesThatCanBeDelete == null)
            {
                m_framesThatCanBeDelete = r_framesInMemory
                    .Values
                    .Where(f => f.LastTimeRead != dateTimeForFrameInMemory)
                    .OrderBy(f => f.Frame.FrameNumber)
                    .ToList();
            }
            if (m_framesThatCanBeDelete?.Count > 0)
            {
                // "Trade" a frame in the cache
                r_framesInMemory.Remove(m_framesThatCanBeDelete.Last().Frame.FrameNumber);
                r_framesInMemory.Add(
                    frameFromFile.FrameNumber,
                    new MotionVectorsFrameWithLastUsedTime(dateTimeForFrameInMemory, frameFromFile));
            }
        }

        private MotionVectorsFrame GetFrameFromMemory(int frameNumber, DateTime dateTimeForFrameInMemory)
        {
            if (r_framesInMemory.TryGetValue(frameNumber, out var frameReference))
            {
                frameReference.LastTimeRead = dateTimeForFrameInMemory;
                return frameReference.Frame;
            }
            else
            {
                return null;
            }
        }

        public void Dispose()
        {
            r_reader.Dispose();
        }
    }
}