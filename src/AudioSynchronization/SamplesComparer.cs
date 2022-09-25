﻿using System;
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
                throw new Exception("NbSamplesPerSecond mismatch"); // TODO
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

        public List<SamplesSectionDiff> Compare()
        {
            DateTime debut = DateTime.Now;
            var matches = new List<SamplesSectionDiff>();
            Compare(matches,
                    new SamplesSection(r_samplesA, 0, r_samplesA.Length),
                    new SamplesSection(r_samplesB, 0, r_samplesB.Length));
            matches.First().ExpendStart(r_samplesA, r_samplesB);
            matches.Last().ExpendEnd(r_samplesA, r_samplesB);

            matches = Cleanup(matches);
            matches.First().ExpendStart(r_samplesA, r_samplesB);
            matches.Last().ExpendEnd(r_samplesA, r_samplesB);

            Console.WriteLine("****** CLEANUP ******");
            double matched = 0;
            foreach (SamplesSectionDiff match in matches)
            {
                matched += match.SectionA.Length;
                Console.WriteLine(match);
            }
            Console.WriteLine("matchA = {0:0.00%}", matched / r_samplesA.Length);
            Console.WriteLine("matchB = {0:0.00%}", matched / r_samplesB.Length);

            Console.Error.WriteLine("TimeSpan = {0}", DateTime.Now - debut);
            return matches;
        }

        private void Compare(
            List<SamplesSectionDiff> matches,
            SamplesSection sectionA, 
            SamplesSection sectionB)
        {
            if (sectionA.Length < r_minimumMatchLength)
                return;
            if (sectionB.Length < r_minimumMatchLength)
                return;

            SamplesSectionDiff bestDiff = null;
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
                        var newDiff = new SamplesSectionDiff(candidateSectionA, candidateSectionB);
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
                    var newDiff = new SamplesSectionDiff(
                                    currentBestDiff.SectionA,
                                    newSectionB);
                    if (newDiff.TotalError < bestDiff.TotalError)
                    {
                        bestDiff = newDiff;
                    }
                }
            }

            Compare(
                matches,
                sectionA.GetSection(0, bestDiff.SectionA.StartIndex - sectionA.StartIndex),
                sectionB.GetSection(0, bestDiff.SectionB.StartIndex - sectionB.StartIndex));

            Console.WriteLine("{0}", bestDiff);
            matches.Add(bestDiff);

            Compare(
                matches,
                sectionA.GetSection(bestDiff.SectionA.StartIndex - sectionA.StartIndex + bestDiff.SectionA.Length),
                sectionB.GetSection(bestDiff.SectionB.StartIndex - sectionB.StartIndex + bestDiff.SectionB.Length));
        }

        private int[] FindLargestPeaks(SamplesSection section)
        {
            // TODO Make this better. Really get minimum peaks per minutes, and remove peaks that are close together
            var nbPeaksMAx = ((section.Length / (r_nbSamplesPerSecond * 60)) + 1) * Options.NbPeaksPerMinute;
            return section
                .GetItems()
                .Where(f => f.Index < section.LastIndex - r_minimumMatchLength)
                .ToArray()
                .OrderByDescending(f => f.DiffFromPrevious)
                .Take(nbPeaksMAx)
                .Select(f => f.Index - section.StartIndex)
                .ToArray();
        }

        private List<SamplesSectionDiff> Cleanup(List<SamplesSectionDiff> matches)
        {
            bool needAnotherCleanup;
            do
            {
                needAnotherCleanup = false;
                var cleanedMatches = new List<SamplesSectionDiff>();
                for (var i = 1; i < matches.Count; i++)
                {
                    var previousDiff = matches[i - 1];
                    var currentDiff = matches[i];

                    var gapA = currentDiff.SectionA.LastIndex - previousDiff.SectionA.StartIndex;
                    var gapB = currentDiff.SectionB.LastIndex - previousDiff.SectionB.StartIndex;
                    var gapToFill = Math.Min(gapA, gapB);
                    var maxGap = Math.Max(gapA, gapB);
                    var isOffsetsTooClose = Math.Abs(previousDiff.Offset - currentDiff.Offset) <= 2;

                    // Compute "Total Samples Difference" using only the Previous Offset
                    var currentSamplesDifference = 0.0;
                    var indexAForPreviousOffset = previousDiff.SectionA.StartIndex;
                    for (int j = 0; j < gapToFill; j++)
                    {
                        // Add sample difference for "PreviousOffset"
                        var indexBForPreviousOffset = indexAForPreviousOffset - previousDiff.Offset;
                        currentSamplesDifference += Math.Abs(r_samplesA[indexAForPreviousOffset].Value - r_samplesB[indexBForPreviousOffset].Value);
                        indexAForPreviousOffset++;
                    }

                    var nbPreviousSamplesToKeep = gapToFill;
                    double bestSamplesDifference = currentSamplesDifference;

                    // Find the best position (i.e. keep X samples from last, and Y samples from current) to minimize the total samples difference
                    var indexAForCurrentOffset = previousDiff.SectionA.StartIndex + maxGap;
                    for (int j = 0; j < gapToFill; j++)
                    {
                        // Remove sample difference for "PreviousOffset" (added in the loop above)
                        indexAForPreviousOffset--;
                        var indexBForPreviousOffset = indexAForPreviousOffset - previousDiff.Offset;
                        currentSamplesDifference -= Math.Abs(r_samplesA[indexAForPreviousOffset].Value - r_samplesB[indexBForPreviousOffset].Value);

                        // Add sample difference for "CurrentOffset"
                        indexAForCurrentOffset--;
                        var indexBForCurrentOffset = indexAForCurrentOffset - currentDiff.Offset;
                        currentSamplesDifference += Math.Abs(r_samplesA[indexAForCurrentOffset].Value - r_samplesB[indexBForCurrentOffset].Value);

                        if (!isOffsetsTooClose && currentSamplesDifference <= bestSamplesDifference)
                        {
                            bestSamplesDifference = currentSamplesDifference;
                            nbPreviousSamplesToKeep = gapToFill - j - 1;
                        }
                    }

                    // If the offset are too close (ex. offset 32123 and 32124), we only compare the total error for "keep all previous offset" or "keep all current offset" (we never 'split' the samples)
                    if (isOffsetsTooClose && currentSamplesDifference <= bestSamplesDifference)
                    {
                        nbPreviousSamplesToKeep = 0;
                    }

                    if (nbPreviousSamplesToKeep < r_minimumMatchLength)
                    {
                        // If the number of samples to keep from the previous section is less then the minimum, we fill the whole gap with the currentDiff.Offset 
                        matches[i - 1] = null;
                        var start = currentDiff.SectionA.LastIndex - gapToFill;
                        matches[i] = new SamplesSectionDiff(
                                    new SamplesSection(r_samplesA, start, gapToFill),
                                    new SamplesSection(r_samplesB, start - currentDiff.Offset, gapToFill));
                        needAnotherCleanup = true;
                    }
                    else
                    {
                        // We split the "gapToFill" samples according to nbPreviousSamplesToKeep
                        cleanedMatches.Add(
                            new SamplesSectionDiff(
                                    new SamplesSection(r_samplesA, previousDiff.SectionA.StartIndex, nbPreviousSamplesToKeep),
                                    new SamplesSection(r_samplesB, previousDiff.SectionA.StartIndex - previousDiff.Offset, nbPreviousSamplesToKeep)));
                        matches[i - 1] = null;

                        var start = currentDiff.SectionA.LastIndex - gapToFill + nbPreviousSamplesToKeep;
                        matches[i] = new SamplesSectionDiff(
                                    new SamplesSection(r_samplesA, start, gapToFill - nbPreviousSamplesToKeep),
                                    new SamplesSection(r_samplesB, start - currentDiff.Offset, gapToFill - nbPreviousSamplesToKeep));
                    }
                }

                if (matches.Last().Length > 0)
                {
                    cleanedMatches.Add(matches.Last());
                }
                matches = cleanedMatches;
            } while (needAnotherCleanup);

            return matches;
        }
    }
}
