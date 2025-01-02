using AudioSynchronization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace FunscriptToolbox.AudioSyncVerbs
{
    internal class TransformedTimeRange
    {
        public AudioSignatureWithLinkedFiles OutputFile { get; set; }
        public TimeSpan RelativeStartTime { get; set; }  // Time relative to the OutputFile
        public TimeSpan RelativeEndTime { get; set; }    // Time relative to the OutputFile
    }

    internal class VirtualMergedAudioOffsetCollection : ReadOnlyCollection<VirtualMergedAudioOffset>
    {
        internal static VirtualMergedAudioOffsetCollection Create(
            AudioOffsetCollection audioOffsets,
            VirtualMergedFile virtualInput,
            VirtualMergedFile virtualOutput)
        {
            var result = new List<VirtualMergedAudioOffset>();
            AudioSignatureWithLinkedFiles currentOutputFile = null;
            TimeSpan lastEndTime = TimeSpan.Zero;

            void AddUnmatchedSection(AudioSignatureWithLinkedFiles outputFile, TimeSpan start, TimeSpan end)
            {
                if (end > start)
                {
                    result.Add(new VirtualMergedAudioOffset
                    {
                        InputFile = null,
                        InputStartTime = null,
                        OutputFile = outputFile,
                        OutputStartTime = start - outputFile.StartTime,
                        Duration = end - start,
                        Offset = null
                    });
                }
            }

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
                            Offset = null
                        });
                        continue;
                    }

                    var outputStartTime = inputRange.StartTime + offset.Offset.Value;
                    var outputEndTime = inputRange.EndTime + offset.Offset.Value;
                    var outputRanges = GetFileRanges(virtualOutput.Files, outputStartTime, outputEndTime);

                    foreach (var outputRange in outputRanges)
                    {
                        // If we've changed output files, handle any unmatched section
                        if (currentOutputFile != outputRange.File)
                        {
                            if (currentOutputFile != null)
                            {
                                // Add unmatched section at the end of the previous file
                                AddUnmatchedSection(
                                    currentOutputFile,
                                    lastEndTime,
                                    currentOutputFile.StartTime + currentOutputFile.Duration);
                            }
                            currentOutputFile = outputRange.File;
                            lastEndTime = currentOutputFile.StartTime;
                        }

                        // Add unmatched section before this match if needed
                        AddUnmatchedSection(currentOutputFile, lastEndTime, outputRange.StartTime);

                        // Calculate the intersection of input and output ranges
                        var intersectStart = inputRange.StartTime;
                        var intersectEnd = inputRange.EndTime;
                        var outputIntersectStart = outputRange.StartTime;

                        if (outputRange.StartTime > outputStartTime)
                        {
                            intersectStart = outputRange.StartTime - offset.Offset.Value;
                            outputIntersectStart = outputRange.StartTime;
                        }
                        if (outputRange.EndTime < outputEndTime)
                        {
                            intersectEnd = outputRange.EndTime - offset.Offset.Value;
                        }

                        result.Add(new VirtualMergedAudioOffset
                        {
                            InputFile = inputRange.File,
                            InputStartTime = intersectStart - inputRange.File.StartTime,
                            OutputFile = outputRange.File,
                            OutputStartTime = outputIntersectStart - outputRange.File.StartTime,
                            Duration = intersectEnd - intersectStart,
                            Offset = offset.Offset
                        });

                        lastEndTime = outputIntersectStart + (intersectEnd - intersectStart);
                    }
                }
            }

            // Handle any unmatched section in the last file
            if (currentOutputFile != null)
            {
                AddUnmatchedSection(currentOutputFile,
                    lastEndTime,
                    currentOutputFile.StartTime + currentOutputFile.Duration);
            }

            // Handle any completely unmatched output files that come after the last matched file
            if (currentOutputFile != null)
            {
                bool foundLastFile = false;
                foreach (var outputFile in virtualOutput.Files)
                {
                    if (foundLastFile)
                    {
                        AddUnmatchedSection(outputFile,
                            outputFile.StartTime,
                            outputFile.StartTime + outputFile.Duration);
                    }
                    else if (outputFile == currentOutputFile)
                    {
                        foundLastFile = true;
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

        public IEnumerable<TransformedTimeRange> TransformTimeRange(ItemType itemType, TimeSpan startTime, TimeSpan endTime)
        {
            foreach (var offset in this)
            {
                if (offset.InputFile == null)
                    continue;

                // Calculate the input time range for this offset
                var offsetInputStart = offset.InputFile.StartTime + offset.InputStartTime.Value;
                var offsetInputEnd = offsetInputStart + offset.Duration;

                // Skip if there's no overlap with this offset's range
                if (startTime >= offsetInputEnd || endTime <= offsetInputStart)
                    continue;

                offset.Usage[itemType]++;

                // Skip dropped content
                if (!offset.Offset.HasValue)
                    continue;

                // Calculate the intersection of the item's time range with this offset's range
                var intersectStart = TimeSpan.FromTicks(Math.Max(startTime.Ticks, offsetInputStart.Ticks));
                var intersectEnd = TimeSpan.FromTicks(Math.Min(endTime.Ticks, offsetInputEnd.Ticks));

                // Calculate how far into the input section we are
                var inputTimeOffset = intersectStart - offsetInputStart;

                // Transform to output time
                var relativeStartTime = offset.OutputStartTime.Value + inputTimeOffset;
                var relativeEndTime = relativeStartTime + (intersectEnd - intersectStart);

                yield return new TransformedTimeRange
                {
                    OutputFile = offset.OutputFile,
                    RelativeStartTime = relativeStartTime,
                    RelativeEndTime = relativeEndTime
                };
            }
        }        
    }
}
