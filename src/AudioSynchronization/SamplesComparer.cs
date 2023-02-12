using System;
using System.Collections.Generic;
using System.Linq;

namespace AudioSynchronization
{
    public class SamplesComparer
    {
        public SamplesComparer(AudioSignature signatureA, AudioSignature signatureB, CompareOptions options)
        {
            if (signatureA.NbSamplesPerSecond != signatureB.NbSamplesPerSecond)
            {
                throw new Exception($"NbSamplesPerSecond mismatch. First audio is: {signatureA.NbSamplesPerSecond}, second audio is: {signatureB.NbSamplesPerSecond}");
            }

            r_samplesA = ComputeItems(signatureA.GetUncompressedSamples());
            r_samplesB = ComputeItems(signatureB.GetUncompressedSamples());
            this.Options = options;
            r_nbSamplesPerSecond = signatureA.NbSamplesPerSecond;
            r_minimumMatchLength = (int) (options.MinimumMatchLength.TotalSeconds * r_nbSamplesPerSecond);
        }

        private readonly Sample[] r_samplesA;
        private readonly Sample[] r_samplesB;
        private readonly int r_nbSamplesPerSecond;
        private readonly int r_minimumMatchLength;

        public CompareOptions Options { get; }

        private Sample[] ComputeItems(ushort[] samples)
        {
            var result = new List<Sample>();

            // Compute Average Values
            var nbAdditionnalSamplesInAverage = 0;
            var i = 0;
            double previousValue = 0;
            foreach (var sample in samples)
            {
                double totalValue = sample;
                var nbValues = 1;
                for (var k = 1; k <= nbAdditionnalSamplesInAverage; k++)
                {
                    if (i - k > 0)
                    {
                        totalValue += samples[i - k];
                        nbValues++;
                    }
                    if (i + k < samples.Length)
                    {
                        totalValue += samples[i + k];
                        nbValues++;
                    }
                }
                var currentValue = totalValue / nbValues;
                result.Add(new Sample
                    {
                        Value = currentValue,
                        DiffFromPrevious = Math.Abs(currentValue - previousValue),
                        Index = i
                    });
                previousValue = currentValue;

                i++;
            }

            return result.ToArray();
        }

        public AudioOffsetCollection FindAudioOffsets(Action<string> logFunc)
        {
            var matches = new List<SamplesSectionMatch>();
            FindMatches(logFunc,
                    matches,
                    new SamplesSection(r_samplesA, r_nbSamplesPerSecond, 0, r_samplesA.Length),
                    new SamplesSection(r_samplesB, r_nbSamplesPerSecond, 0, r_samplesB.Length));
            matches.First().ExpendStart();
            matches.Last().ExpendEnd(r_samplesA, r_samplesB);

            matches = Cleanup(logFunc, matches);
            matches.First().ExpendStart();
            matches.Last().ExpendEnd(r_samplesA, r_samplesB);

            logFunc?.Invoke("****** FINAL ******");
            double matched = 0;
            foreach (SamplesSectionMatch match in matches)
            {
                matched += match.SectionA.Length;
                logFunc?.Invoke(match.ToString());
            }
            logFunc?.Invoke($"matchA = {matched / r_samplesA.Length:0.00%}");
            logFunc?.Invoke($"matchB = {matched / r_samplesB.Length:0.00%}");

            // Transform SamplesSectionMatch into AudioOffset, and fill the remaining gap of SectionA with "null offset".
            var result = new List<AudioOffset>();
            var currentIndex = 0;
            foreach (SamplesSectionMatch match in matches)
            {
                if (match.SectionA.StartIndex > currentIndex)
                {
                    result.Add(
                        new AudioOffset(
                            ConvertIndexToTimeSpan(currentIndex),
                            ConvertIndexToTimeSpan(match.SectionA.StartIndex), 
                            null));
                }
                result.Add(
                    new AudioOffset(
                        ConvertIndexToTimeSpan(match.SectionA.StartIndex),
                        ConvertIndexToTimeSpan(match.SectionA.EndIndex),
                        ConvertIndexToTimeSpan(match.Offset)));
                currentIndex = match.SectionA.EndIndex;
            }
            if (currentIndex < r_samplesA.Length)
            {
                result.Add(
                    new AudioOffset(
                        ConvertIndexToTimeSpan(currentIndex),
                        ConvertIndexToTimeSpan(r_samplesA.Length),
                        null));
            }

            return new AudioOffsetCollection(result);
        }

        private TimeSpan ConvertIndexToTimeSpan(int index) => TimeSpan.FromSeconds((double)index / r_nbSamplesPerSecond);

        private void FindMatches(
            Action<string> logFunc,
            List<SamplesSectionMatch> matches,
            SamplesSection sectionA, 
            SamplesSection sectionB)
        {
            if (sectionA.Length < r_minimumMatchLength)
                return;
            if (sectionB.Length < r_minimumMatchLength)
                return;

            SamplesSectionMatch bestDiff = null;
            var peaksA = FindLargestPeaks(sectionA);
            var peaksB = FindLargestPeaks(sectionB);
            foreach (var indexA in peaksA)
            {
                foreach (var indexB in peaksB)
                {
                    var candidateSectionA = sectionA.GetSection(indexA, r_minimumMatchLength);
                    var candidateSectionB = sectionB.GetSection(indexB, r_minimumMatchLength);
                    if (candidateSectionA.Length >= r_minimumMatchLength && candidateSectionB.Length >= r_minimumMatchLength)
                    {
                        var newDiff = new SamplesSectionMatch(candidateSectionA, candidateSectionB);
                        if (bestDiff == null || newDiff.TotalError < bestDiff.TotalError)
                        {
                            bestDiff = newDiff;
                        }
                    }
                }
            }

            if (bestDiff == null)
                return;

            // Optimize BestDiff
            var currentBestDiff = bestDiff;
            foreach (var offsetB in new int[] { -3, -2, -1, 1, 2, 3 })
            {
                var newSectionB = currentBestDiff.SectionB.GetOffsetedSection(offsetB);
                if (newSectionB != null)
                {
                    var newDiff = new SamplesSectionMatch(
                                    currentBestDiff.SectionA,
                                    newSectionB);
                    if (newDiff.TotalError < bestDiff.TotalError)
                    {
                        bestDiff = newDiff;
                    }
                }
            }

            // If the offset is almost 0, force it 0
            if (Math.Abs(bestDiff.Offset) <= 1)
            {
                bestDiff = new SamplesSectionMatch(
                    bestDiff.SectionA,
                    bestDiff.SectionB.GetOffsetedSection(-bestDiff.Offset));
            }

            FindMatches(
                logFunc,
                matches,
                sectionA.GetSection(0, bestDiff.SectionA.StartIndex - sectionA.StartIndex),
                sectionB.GetSection(0, bestDiff.SectionB.StartIndex - sectionB.StartIndex));

            logFunc?.Invoke(bestDiff.ToString());
            matches.Add(bestDiff);

            FindMatches(
                logFunc,
                matches,
                sectionA.GetSection(bestDiff.SectionA.StartIndex - sectionA.StartIndex + bestDiff.SectionA.Length),
                sectionB.GetSection(bestDiff.SectionB.StartIndex - sectionB.StartIndex + bestDiff.SectionB.Length));
        }

        private int[] FindLargestPeaks(SamplesSection section)
        {
            // TODO Make this better. Really get minimum peaks per minutes, and remove peaks that are close together
            var nbPeaksMAx = ((section.Length / (r_nbSamplesPerSecond * 60)) + 1) * Options.NbLocationsPerMinute;
            return section
                .GetItems()
                .Where(f => f.Index < section.EndIndex - r_minimumMatchLength)
                .ToArray()
                .OrderByDescending(f => f.DiffFromPrevious)
                .Take(nbPeaksMAx)
                .Select(f => f.Index - section.StartIndex)
                .ToArray();
        }

        private List<SamplesSectionMatch> Cleanup(
            Action<string> logFunc,
            List<SamplesSectionMatch> matches)
        {
            bool needAnotherCleanup;
            do
            {
                needAnotherCleanup = false;
                var cleanedMatches = new List<SamplesSectionMatch>();
                for (var i = 1; i < matches.Count; i++)
                {
                    var previousDiff = matches[i - 1];
                    var currentDiff = matches[i];

                    var gapA = currentDiff.SectionA.EndIndex - previousDiff.SectionA.StartIndex;
                    var gapB = currentDiff.SectionB.EndIndex - previousDiff.SectionB.StartIndex;
                    var gapToFill = Math.Min(gapA, gapB);
                    var isOffsetsTooClose = Math.Abs(previousDiff.Offset - currentDiff.Offset) <= 2;

                    // Compute "Total Samples Difference" using only the Previous Offset
                    var currentSamplesDifference = 0.0;
                    var indexAForPreviousOffset = previousDiff.SectionA.StartIndex;
                    for (int j = 0; j < gapToFill; j++)
                    {
                        // Add sample difference for "PreviousOffset"
                        var indexBForPreviousOffset = indexAForPreviousOffset + previousDiff.Offset;
                        currentSamplesDifference += Math.Abs(r_samplesA[indexAForPreviousOffset].Value - r_samplesB[indexBForPreviousOffset].Value);
                        indexAForPreviousOffset++;
                    }

                    var bestPreviousSamplesToKeep = gapToFill;
                    double bestSamplesDifference = currentSamplesDifference;

                    // Find the best position (i.e. keep X samples from last, and Y samples from current) to minimize the total samples difference
                    var indexAForCurrentOffset = currentDiff.SectionB.EndIndex - currentDiff.Offset;
                    for (int currentPreviousSamplesToKeep = gapToFill - 1; currentPreviousSamplesToKeep >= 0; currentPreviousSamplesToKeep--)
                    {
                        // Remove sample difference for "PreviousOffset" (added in the loop above)
                        indexAForPreviousOffset--;
                        var indexBForPreviousOffset = indexAForPreviousOffset + previousDiff.Offset;
                        currentSamplesDifference -= Math.Abs(r_samplesA[indexAForPreviousOffset].Value - r_samplesB[indexBForPreviousOffset].Value);

                        // Add sample difference for "CurrentOffset"
                        indexAForCurrentOffset--;
                        var indexBForCurrentOffset = indexAForCurrentOffset + currentDiff.Offset;
                        currentSamplesDifference += Math.Abs(r_samplesA[indexAForCurrentOffset].Value - r_samplesB[indexBForCurrentOffset].Value);

                        if (!isOffsetsTooClose && currentSamplesDifference <= bestSamplesDifference)
                        {
                            bestPreviousSamplesToKeep = currentPreviousSamplesToKeep;
                            bestSamplesDifference = currentSamplesDifference;
                        }
                    }

                    // If the offset are too close (ex. offset 32123 and 32124), we only compare the total error for "keep all previous offset" or "keep all current offset" (we never 'split' the samples)
                    if (isOffsetsTooClose && currentSamplesDifference <= bestSamplesDifference)
                    {
                        bestPreviousSamplesToKeep = 0;
                    }

                    if (bestPreviousSamplesToKeep < r_minimumMatchLength)
                    {
                        // If the number of samples to keep from the previous section is less then the minimum, we fill the whole gap with the currentDiff.Offset 
                        matches[i - 1] = null;
                        var start = currentDiff.SectionA.EndIndex - gapToFill;
                        matches[i] = new SamplesSectionMatch(
                                    new SamplesSection(r_samplesA, r_nbSamplesPerSecond, start, gapToFill),
                                    new SamplesSection(r_samplesB, r_nbSamplesPerSecond, start + currentDiff.Offset, gapToFill));
                        needAnotherCleanup = true;
                    }
                    else
                    {
                        // We split the "gapToFill" samples according to nbPreviousSamplesToKeep
                        cleanedMatches.Add(
                            new SamplesSectionMatch(
                                    new SamplesSection(r_samplesA, r_nbSamplesPerSecond, previousDiff.SectionA.StartIndex, bestPreviousSamplesToKeep),
                                    new SamplesSection(r_samplesB, r_nbSamplesPerSecond, previousDiff.SectionA.StartIndex + previousDiff.Offset, bestPreviousSamplesToKeep)));
                        matches[i - 1] = null;

                        var start = currentDiff.SectionA.EndIndex - gapToFill + bestPreviousSamplesToKeep;
                        matches[i] = new SamplesSectionMatch(
                                    new SamplesSection(r_samplesA, r_nbSamplesPerSecond, start, gapToFill - bestPreviousSamplesToKeep),
                                    new SamplesSection(r_samplesB, r_nbSamplesPerSecond, start + currentDiff.Offset, gapToFill - bestPreviousSamplesToKeep));
                    }
                }

                if (matches.Last().Length > 0)
                {
                    cleanedMatches.Add(matches.Last());
                }

                logFunc?.Invoke($"------- Merging blocks from {matches.Count()} to {cleanedMatches.Count()} ----");
                foreach (var cleanMatch in cleanedMatches)
                {
                    logFunc?.Invoke(cleanMatch.ToString());
                }

                matches = cleanedMatches;
            } while (needAnotherCleanup);

            return matches;
        }
    }
}
