using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class TranslatorSubtitleConformer : Translator
    {
        [JsonProperty(Order = 20, Required = Required.Always)]
        internal MetadataAggregator Metadatas { get; set; }

        [JsonProperty(Order = 21, Required = Required.Always)]
        public AIOptions Options { get; set; }

        [JsonProperty(Order = 22)]
        public string MetadataSpeaker { get; set; } = "Speaker";

        [JsonProperty(Order = 30)]
        public int MaxLineLength { get; set; } = 50;

        [JsonProperty(Order = 31)]
        public TimeSpan MaxMergeGapSameSpeaker { get; set; } = TimeSpan.FromSeconds(1);

        [JsonProperty(Order = 32)]
        public TimeSpan MaxMergeGapDialogue { get; set; } = TimeSpan.FromSeconds(1.2);

        [JsonProperty(Order = 33)]
        public TimeSpan MaxMergeDuration { get; set; } = TimeSpan.FromSeconds(6);

        [JsonProperty(Order = 34)]
        public bool RemoveMissing { get; set; } = false;

        [JsonProperty(Order = 35)]
        public bool AddReviewIfTooLong { get; set; } = true;

        [JsonProperty(Order = 40)]
        public string[] SplitPatterns { get; set; } = new[]
            {
                @"([,.?!。、！？-]*)" + CutCharacter,
                CutCharacter + @"\b(and|but|or)\b",
                CutCharacter + @"\s"
            };

        private const string CutCharacter = "✂️";
        private const string FLAG_MISSING = "[!MISSING]";
        private const string FLAG_REVIEW = "[!REVIEW]";
        private readonly (bool isSplitBefore, Regex regex)[] r_splitRegexes;

        public TranslatorSubtitleConformer()
        {
            r_splitRegexes = this.SplitPatterns.Select(pattern => pattern.StartsWith(CutCharacter)
                        ? (true, new Regex(pattern.Substring(1), RegexOptions.Compiled))
                        : (false, new Regex(pattern.Substring(0, pattern.Length - 1), RegexOptions.Compiled)))
                .ToArray();
        }

        protected override string GetMetadataProduced() => this.Options.MetadataAlwaysProduced;

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (this.Metadatas.Aggregate(context).IsPrerequisitesMetWithTimings(out reason) == false)
            {
                return false;
            }

            reason = null;
            return true;
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Translation translation)
        {
            var requestGenerator = this.Metadatas
                .Aggregate(context, translation)
                .CreateRequestGenerator(translation, this.Options, translationLanguage: this.TargetLanguage);

            var (_, itemsToDo, _, _) = requestGenerator.AnalyzeItemsState();

            translation.Items.Clear();
            translation.Items.AddRange(Conform(itemsToDo)
                .Where(f => f != null)
                .Where(f => !this.RemoveMissing || f.Metadata.Get(this.Options.MetadataAlwaysProduced) != FLAG_MISSING));
            translation.MarkAsFinished();
            context.WIP.Save();
        }

        public TranslatedItem[] Conform(IList<TimedItemWithMetadata> nodes)
        {
            if (nodes == null || nodes.Count == 0) return Array.Empty<TranslatedItem>();

            var newNodes = nodes
                .Select(node =>
                {
                    var original = node.Metadata.Get(this.Options.MetadataNeeded);
                    return new TranslatedItem(
                            node.StartTime,
                            node.EndTime,
                            MetadataCollection.CreateSimple(
                                this.Options.MetadataAlwaysProduced,
                                string.IsNullOrWhiteSpace(original) 
                                ? FLAG_MISSING 
                                : original.Trim()));
                })
                .ToArray();

            // 2. Sequential Merge Pass (note: we use both the original nodes (to have acces to Speaker) and the new nodes.
            for (int i = 0; i < nodes.Count - 1; i++)
            {
                if (newNodes[i] == null
                    || newNodes[i + 1] == null
                    || newNodes[i].Metadata.Get(this.Options.MetadataAlwaysProduced) == FLAG_MISSING
                    || newNodes[i + 1].Metadata.Get(this.Options.MetadataAlwaysProduced) == FLAG_MISSING) 
                    continue;

                var current = nodes[i];
                var next = nodes[i + 1];
                var mergedText = MergeSameSpeaker(current, next) ?? MergeDialogue(current, next);
                if (mergedText != null)
                {
                    newNodes[i].Metadata[this.Options.MetadataAlwaysProduced] = mergedText;
                    newNodes[i].EndTime = newNodes[i + 1].EndTime;
                    newNodes[i + 1] = null;
                }
            }

            // 3. Final Conformity Pass
            foreach (var node in newNodes)
            {
                if (node == null) continue;
                ApplyPhysicalConstraints(node);
            }

            return newNodes;
        }

        private string MergeSameSpeaker(TimedItemWithMetadata n1, TimedItemWithMetadata n2)
        {
            var s1 = n1.Metadata.Get(this.MetadataSpeaker) ?? "undefined";
            var s2 = n2.Metadata.Get(this.MetadataSpeaker) ?? "undefined";
            if (!string.Equals(s1, s2, StringComparison.OrdinalIgnoreCase)) return null;

            if ((n2.StartTime - n1.EndTime) >= this.MaxMergeGapSameSpeaker) return null;

            var totalDuration = n2.EndTime - n1.StartTime;
            if (totalDuration >= this.MaxMergeDuration) return null;

            var text1 = n1.Metadata.Get(this.Options.MetadataNeeded);
            var text2 = n2.Metadata.Get(this.Options.MetadataNeeded);

            if (text1.Length > this.MaxLineLength) return null;
            if (text2.Length > this.MaxLineLength) return null;

            return $"{text1}\n{text2}";
        }

        private string MergeDialogue(TimedItemWithMetadata n1, TimedItemWithMetadata n2)
        {
            var s1 = n1.Metadata.Get(this.MetadataSpeaker) ?? "undefined";
            var s2 = n2.Metadata.Get(this.MetadataSpeaker) ?? "undefined";
            if (string.Equals(s1, s2, StringComparison.OrdinalIgnoreCase)) return null;

            if ((n2.StartTime - n1.EndTime) >= this.MaxMergeGapDialogue) return null;

            var text1 = n1.Metadata.Get(this.Options.MetadataNeeded);
            var text2 = n2.Metadata.Get(this.Options.MetadataNeeded);

            // Dialogue formatting adds "- ", so we need to account for that (length + 2)
            if ((text1.Length + 2) > this.MaxLineLength) return null;
            if ((text2.Length + 2) > this.MaxLineLength) return null;

            return $"- {text1}\n- {text2}";
        }

        private void ApplyPhysicalConstraints(TimedItemWithMetadata node)
        {
            var text = node.Metadata.Get(this.Options.MetadataAlwaysProduced);

            if (text.Length <= this.MaxLineLength) return;

            // If already contains newline, assume it was formatted by merge or previous pass
            if (text.Contains("\n")) return;

            var splitText = InsertOptimalLineBreak(text);
            if (this.AddReviewIfTooLong && text.Length > this.MaxLineLength * 2 && !splitText.Contains(FLAG_REVIEW))
            {
                splitText += FLAG_REVIEW;
            }
            node.Metadata[this.Options.MetadataAlwaysProduced] = splitText;

            Console.WriteLine(text);
            Console.WriteLine(splitText);
            Console.WriteLine(new string('=', this.MaxLineLength));
            var m = 0;
        }

        private string InsertOptimalLineBreak(string text)
        {
            // The split index must ensure both resulting lines are <= MaxLineLength.
            // Split Index Window: [Length - Max, Max]
            int minIndex = Math.Max(0, text.Length - this.MaxLineLength);
            int maxIndex = Math.Min(text.Length, this.MaxLineLength);

            if (minIndex >= maxIndex)
            {
                var x = maxIndex;
                maxIndex = minIndex;
                minIndex = x;
            }

            foreach (var item in r_splitRegexes)
            {
                var bestSplitIndex = FindBestSplitIndex(text, item.regex, minIndex, maxIndex, item.isSplitBefore);
                if (bestSplitIndex != null)
                {
                    return CleanSplit(text, bestSplitIndex.Value);
                }
            }

            return text + FLAG_REVIEW;
        }

        /// <summary>
        /// Finds the regex match within the [min, max] window that is closest to the middle of the string.
        /// </summary>
        private int? FindBestSplitIndex(string text, Regex regex, int minIndex, int maxIndex, bool isSplitBefore)
        {
            var matches = regex.Matches(text);
            int idealMiddle = text.Length / 2;
            int? bestIdx = null;
            int bestDist = int.MaxValue;

            foreach (Match m in matches)
            {
                // Calculate the potential split point based on strategy
                int potentialSplitPoint = isSplitBefore ? m.Index : (m.Index + m.Length);

                // Check if this point creates valid line lengths (is inside the window)
                if (potentialSplitPoint >= minIndex && potentialSplitPoint <= maxIndex)
                {
                    // Calculate distance from middle
                    int dist = Math.Abs(potentialSplitPoint - idealMiddle);

                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestIdx = potentialSplitPoint;
                    }
                }
            }

            return bestIdx;
        }

        private string CleanSplit(string text, int index)
        {
            // Trimming handles the removal of the space if we split on a space,
            // or ensures no leading/trailing whitespace around the break.
            string first = text.Substring(0, index).Trim();
            string second = text.Substring(index).Trim();
            return $"{first}\n{second}";
        }
    }
}