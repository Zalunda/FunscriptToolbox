using AudioSynchronization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FunscriptToolbox.AudioSyncVerbs
{
    internal class VirtualMergedAudioOffsetCollection : ReadOnlyCollection<VirtualMergedAudioOffset>
    {
        internal static VirtualMergedAudioOffsetCollection Create(
            AudioOffsetCollection audioOffsets,
            VirtualMergedFile virtualInput,
            VirtualMergedFile virtualOutput)
        {
            var result = new List<VirtualMergedAudioOffset>();

            foreach (var offset in audioOffsets)
            {
                var inputRanges = GetFileRanges(virtualInput.Files, offset.StartTime, offset.StartTime + offset.Duration);

                foreach (var inputRange in inputRanges)
                {
                    if (!offset.Offset.HasValue)
                    {
                        // For dropped content, create one entry per input file section
                        result.Add(new VirtualMergedAudioOffset
                        {
                            InputFile = inputRange.File,
                            InputStartTime = inputRange.StartTime - inputRange.File.StartTime,
                            OutputFile = null,
                            OutputStartTime = null,
                            Duration = inputRange.Duration,
                            Offset = null,
                            NbTimesUsed = offset.NbTimesUsed
                        });
                        continue;
                    }

                    var outputStartTime = inputRange.StartTime + offset.Offset.Value;
                    var outputEndTime = inputRange.EndTime + offset.Offset.Value;
                    var outputRanges = GetFileRanges(virtualOutput.Files, outputStartTime, outputEndTime);

                    foreach (var outputRange in outputRanges)
                    {
                        // Calculate the intersection of input and output ranges
                        var intersectStart = inputRange.StartTime;
                        var intersectEnd = inputRange.EndTime;
                        var outputIntersectStart = outputRange.StartTime;

                        if (outputRange.StartTime > outputStartTime)
                        {
                            // Adjust input range start to match output file boundary
                            intersectStart = outputRange.StartTime - offset.Offset.Value;
                            outputIntersectStart = outputRange.StartTime;
                        }
                        if (outputRange.EndTime < outputEndTime)
                        {
                            // Adjust input range end to match output file boundary
                            intersectEnd = outputRange.EndTime - offset.Offset.Value;
                        }

                        result.Add(new VirtualMergedAudioOffset
                        {
                            InputFile = inputRange.File,
                            InputStartTime = intersectStart - inputRange.File.StartTime,
                            OutputFile = outputRange.File,
                            OutputStartTime = outputIntersectStart - outputRange.File.StartTime,
                            Duration = intersectEnd - intersectStart,
                            Offset = offset.Offset,
                            NbTimesUsed = offset.NbTimesUsed
                        });
                    }
                }
            }

            return new VirtualMergedAudioOffsetCollection(result);
        }

        private class FileRange
        {
            public AudioSignatureWithLinkedFiles File { get; set; }
            public TimeSpan StartTime { get; set; }
            public TimeSpan EndTime { get; set; }
            public TimeSpan Duration => EndTime - StartTime;
        }

        private static List<FileRange> GetFileRanges(
            AudioSignatureWithLinkedFiles[] files,
            TimeSpan startTime,
            TimeSpan endTime)
        {
            var ranges = new List<FileRange>();

            foreach (var file in files)
            {
                var fileStart = file.StartTime;
                var fileEnd = file.StartTime + file.Duration;

                if (startTime < fileEnd && endTime > fileStart)
                {
                    ranges.Add(new FileRange
                    {
                        File = file,
                        StartTime = TimeSpan.FromTicks(Math.Max(startTime.Ticks, fileStart.Ticks)),
                        EndTime = TimeSpan.FromTicks(Math.Min(endTime.Ticks, fileEnd.Ticks))
                    });
                }
            }

            return ranges;
        }

        public VirtualMergedAudioOffsetCollection(IList<VirtualMergedAudioOffset> list) : base(list)
        {
        }
    }

    internal class VirtualMergedAudioOffsetCollection2 : ReadOnlyCollection<VirtualMergedAudioOffset>
    {
        internal static VirtualMergedAudioOffsetCollection2 Create(
            AudioOffsetCollection audioOffsets, 
            VirtualMergedFile virtualInput, 
            VirtualMergedFile virtualOutput)
        {
            var result = new List<VirtualMergedAudioOffset>();

            foreach (var offset in audioOffsets)
            {
                var sourceFile = virtualInput.Files.FirstOrDefault(f =>
                    offset.StartTime >= f.StartTime &&
                    offset.StartTime < (f.StartTime + f.Duration));

                var destinationTime = offset.Offset.HasValue ?
                    offset.StartTime + offset.Offset.Value :
                    offset.StartTime;

                var destinationFile = virtualOutput.Files.FirstOrDefault(f =>
                    destinationTime >= f.StartTime &&
                    destinationTime < (f.StartTime + f.Duration));

                result.Add(new VirtualMergedAudioOffset
                {
                    InputFile = sourceFile,
                    InputStartTime = sourceFile != null ?
                        offset.StartTime - sourceFile.StartTime :
                        offset.StartTime,
                    OutputFile = destinationFile,
                    OutputStartTime = destinationFile != null && offset.Offset.HasValue ?
                        destinationTime - destinationFile.StartTime :
                        TimeSpan.Zero,
                    Duration = offset.Duration,
                    Offset = offset.Offset,
                    NbTimesUsed = offset.NbTimesUsed
                });
            }

            return new VirtualMergedAudioOffsetCollection2(result);
        }

        public VirtualMergedAudioOffsetCollection2(IList<VirtualMergedAudioOffset> list) : base(list)
        {
        }
    }
}
