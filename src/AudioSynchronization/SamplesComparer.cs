using System;
using System.Collections.Generic;
using System.Diagnostics;
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

            r_nbSamplesPerSecond = signatureA.NbSamplesPerSecond;
            var nbSamplesOffsetForDiff = Math.Min(1, r_nbSamplesPerSecond / 20);
            r_samplesA = ComputeItems(nbSamplesOffsetForDiff, signatureA.UncompressedSamples);
            r_samplesB = ComputeItems(nbSamplesOffsetForDiff, signatureB.UncompressedSamples);
            r_minimumMatchLength = (int)(options.MinimumMatchLength.TotalSeconds * r_nbSamplesPerSecond);
            this.Options = options;
        }

        private readonly int r_nbSamplesPerSecond;
        private readonly Sample[] r_samplesA;
        private readonly Sample[] r_samplesB;
        private readonly int r_minimumMatchLength;

        public CompareOptions Options { get; }

        private static Sample[] ComputeItems(int nbSamplesOffsetForDiff, ushort[] samples)
        {
            return samples.Select(
                (sample, index) => new Sample
                {
                    Value = sample,
                    DiffFromPrevious = (index < nbSamplesOffsetForDiff) ? 0 : sample - samples[index - nbSamplesOffsetForDiff],
                    Index = index
                }).ToArray();
        }

        public AudioOffsetCollection FindAudioOffsets(Action<string> logFunc)
        {
            var unsortedMatches = new List<SamplesSectionMatch>();
            FindMatches(logFunc,
                    unsortedMatches,
                    new SamplesSection(r_samplesA, r_nbSamplesPerSecond, 0, r_samplesA.Length),
                    new SamplesSection(r_samplesB, r_nbSamplesPerSecond, 0, r_samplesB.Length));
            var matches = unsortedMatches.OrderBy(f => f.SectionA.StartIndex).ToList();
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

            matches.Add(bestDiff);
            var matchCount = matches.Count;

            FindMatches(
                logFunc,
                matches,
                sectionA.GetSection(0, bestDiff.SectionA.StartIndex - sectionA.StartIndex),
                sectionB.GetSection(0, bestDiff.SectionB.StartIndex - sectionB.StartIndex));

            logFunc?.Invoke($"{matchCount,-3} {bestDiff}");

            FindMatches(
                logFunc,
                matches,
                sectionA.GetSection(bestDiff.SectionA.StartIndex - sectionA.StartIndex + bestDiff.SectionA.Length),
                sectionB.GetSection(bestDiff.SectionB.StartIndex - sectionB.StartIndex + bestDiff.SectionB.Length));
        }

        private int[] FindLargestPeaks(SamplesSection section)
        {
            var nbMinutesInSection = (section.Length / (r_nbSamplesPerSecond * 60)) + 1;
            int nbReturn;
            int offsetForDiff;
            if (nbMinutesInSection > 15)
            {
                nbReturn = Options.NbLocationsPerMinute;
                offsetForDiff = r_nbSamplesPerSecond;
            }
            else if (nbMinutesInSection >= 3)
            {
                nbReturn = Options.NbLocationsPerMinute * 2;
                offsetForDiff = r_nbSamplesPerSecond / 2;
            }
            else
            {
                nbReturn = Options.NbLocationsPerMinute * 3;
                offsetForDiff = r_nbSamplesPerSecond / 3;
            }
            var k = section
                .GetItems()
                .GroupBy(f => f.Index / (r_nbSamplesPerSecond * 60))
                .Select(itemsPerMinute => FindLargestPeaksInGroup(itemsPerMinute, nbReturn, offsetForDiff))
                .SelectMany(batch => batch)
                .Select(f => f.Index - section.StartIndex)
                .OrderBy(f => f)
                .ToArray();
            return k;
        }

        private static IEnumerable<Sample> FindLargestPeaksInGroup(IGrouping<int, Sample> itemsPerMinute, int nbToReturn, int offsetForDiff)
        {
            var result = new List<Sample>();
            foreach (var item in itemsPerMinute.OrderByDescending(f => f.DiffFromPrevious))
            {
                if (!result.Any(f => Math.Abs(f.Index - item.Index) < offsetForDiff))
                {
                    yield return item;
                    result.Add(item);
                    if (result.Count == nbToReturn)
                    {
                        yield break;
                    }
                }
            }
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
                    var bestLargestBlock = gapToFill;
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

                        // Check if it's better then the previous best. If it equal, we use the one that create the biggest block.
                        var newLargestBlock = Math.Max(currentPreviousSamplesToKeep, gapToFill - currentPreviousSamplesToKeep);
                        if (currentSamplesDifference < bestSamplesDifference 
                            || (currentSamplesDifference == bestSamplesDifference && newLargestBlock > bestLargestBlock))
                        {
                            bestPreviousSamplesToKeep = currentPreviousSamplesToKeep;
                            bestLargestBlock = newLargestBlock;
                            bestSamplesDifference = currentSamplesDifference;
                        }
                    }

                    if (bestPreviousSamplesToKeep == 0 || previousDiff.Offset == currentDiff.Offset)
                    {
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
