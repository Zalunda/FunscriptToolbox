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
        public int FormatType { get; }
        public int FormatVersion { get; }

        public MotionVectorsFrameLayout FrameLayout { get; }
        public int VideoWidth => FrameLayout.Width;
        public int VideoHeight => FrameLayout.Height;
        public TimeSpan VideoDuration { get; }
        public decimal VideoFramerate { get; }
        public double FrameDurationInMs => 1000.0 / (double)VideoFramerate;
        public int NbFrames { get; }
        public int SensorWidth { get; }
        public int SensorHeight { get; }

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
            if (shouldBeFTMV != "FMVS")
            {
                throw new Exception($"Trying to open .mvs file '{filepath}' but it's not in .mvs format.");
            }
            FormatType = r_reader.ReadByte();
            if (FormatType != 2)
            {
                throw new Exception($"Trying to open .mvs file '{filepath}' but the type is {FormatType} instead of 2 (Matrix).");
            }
            FormatVersion = r_reader.ReadByte();

            var videoWidth = r_reader.ReadInt16();
            var videoHeight = r_reader.ReadInt16();
            VideoDuration = TimeSpan.FromMilliseconds(r_reader.ReadInt32());
            VideoFramerate = (decimal)r_reader.ReadInt32() / 100;
            NbFrames = r_reader.ReadInt32();
            var cellWidth = r_reader.ReadInt16();
            var cellHeight = r_reader.ReadInt16();
            SensorWidth = r_reader.ReadInt16();
            SensorHeight = r_reader.ReadInt16();
            var nbColumns = r_reader.ReadInt16();
            var nbRows = r_reader.ReadInt16();
            r_reader.ReadBytes(24); // Read "forFutureUse" bytes
            this.FrameLayout = new MotionVectorsFrameLayout(videoWidth, videoHeight, cellWidth, cellHeight, nbColumns, nbRows);

            r_maximumMemoryUsage = maximumMemoryUsageInMB * 1024 * 1024;
            r_posAfterHeader = r_reader.BaseStream.Position;
            r_frameSize = this.FrameLayout.NbCellsTotalPerFrame * 2 + 20; // 20 => Taille du headers (int=4, int=4, byte=1, future_use=11)
            r_framesInMemory = new Dictionary<int, MotionVectorsFrameWithLastUsedTime>();
            m_memoryUsed = 0;
        }

        private class MotionVectorsFrameWithLastUsedTime
        {
            public MotionVectorsFrameWithLastUsedTime(DateTime dateTimeForFrameInMemory, MotionVectorsFrame<CellMotionSByte> frameFromFile)
            {
                LastTimeRead = dateTimeForFrameInMemory;
                Frame = frameFromFile;
            }

            public DateTime LastTimeRead { get; set; }
            public MotionVectorsFrame<CellMotionSByte> Frame { get; }
        }

        public int GetFrameNumberFromTime(TimeSpan time)
        {
            return 1 + (int)Math.Round(time.TotalSeconds / (double)(1 / VideoFramerate));
        }

        public IEnumerable<MotionVectorsFrame<CellMotionSByte>> ReadFrames(TimeSpan start, TimeSpan end)
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

        public IEnumerable<MotionVectorsFrame<CellMotionSByte>> ReadFrames(int startingFrameNumber = 1, DateTime dateTimeForFrameInMemory = default)
        {
            r_reader.BaseStream.Seek(r_posAfterHeader + ((long)startingFrameNumber - 1) * r_frameSize, SeekOrigin.Begin);

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
                    var frameFromFile = new MotionVectorsFrame<CellMotionSByte>(
                        this.FrameLayout,
                        frameNumberInFile, 
                        frameTimeInMsInFile,
                        frameType,
                        ReadMotions());

                    TryAddFrameToMemory(frameFromFile, dateTimeForFrameInMemory);
                    yield return frameFromFile;
                }

                currentFrameNumber++;
            }
        }

        private CellMotionSByte[] ReadMotions()
        {
            var motions = new CellMotionSByte[this.FrameLayout.NbCellsTotalPerFrame];
            unsafe
            {
                fixed (void* destination = &motions[0])
                {
                    // Read all motion vectors at once
                    byte[] buffer = r_reader.ReadBytes(this.FrameLayout.NbCellsTotalPerFrame * 2); // 2 bytes per cell
                    fixed (void* source = &buffer[0])
                    {
                        Buffer.MemoryCopy(source, destination, this.FrameLayout.NbCellsTotalPerFrame * 2, this.FrameLayout.NbCellsTotalPerFrame * 2);
                    }
                }
            }
            return motions;
        }

        private void TryAddFrameToMemory(MotionVectorsFrame<CellMotionSByte> frameFromFile, DateTime dateTimeForFrameInMemory)
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

        private MotionVectorsFrame<CellMotionSByte> GetFrameFromMemory(int frameNumber, DateTime dateTimeForFrameInMemory)
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