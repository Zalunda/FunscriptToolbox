using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIRequestGenerator
    {
        private readonly TimedItemWithMetadata[] r_referenceTimings;
        private readonly TimedItemWithMetadataCollection r_workingOnContainer;
        private readonly string r_systemPrompt;
        private readonly string r_userPrompt;
        private readonly string[] r_metadataNeededRules;
        private readonly string[] r_metadataProducedRule;
        private readonly AIOptions r_options;

        private readonly DateTime r_processStartTime;

        public AIRequestGenerator(
            TimedItemWithMetadata[] referenceTimings,
            TimedItemWithMetadataCollection workingOnContainer,
            Language transcriptionLanguage,
            Language translationLanguage,
            AIOptions options = null)
        {
            options = options ?? new AIOptions();

            if (options.MaximumRequestsInBatch > 1 && (options.NbContextItems ?? 0) > 0)
            {
                throw new InvalidOperationException(
                    $"Configuration error on '{workingOnContainer?.Id}': MaximumRequestsInBatch be > 1 when NbContextItems > 0 (current value: {options.NbContextItems}). Set NbContextItems to 0 or null when using MaximumRequestsInBatch > 1.");
            }

            r_referenceTimings = referenceTimings ?? Array.Empty<TimedItemWithMetadata>();
            r_workingOnContainer = workingOnContainer;

            r_systemPrompt = options.SystemPrompt?.GetFinalText(transcriptionLanguage, translationLanguage);
            r_userPrompt = options.UserPrompt?.GetFinalText(transcriptionLanguage, translationLanguage);
            r_metadataNeededRules = options.MetadataNeeded?.Split(',').Select(f => f.Trim()).ToArray();
            r_metadataProducedRule = options.MetadataAlwaysProduced?.Split(',').Select(f => f.Trim()).ToArray();

            r_options = options;
            r_processStartTime = DateTime.Now;
        }

        public (TimedItemWithMetadataTagged[] allItems, TimedItemWithMetadataTagged[] itemsToDo, TimedItemWithMetadataTagged[] itemAlreadyDone, TimedItemWithMetadataTagged[] itemsForTraining) AnalyzeItemsState()
        {
            var allItems = new List<TimedItemWithMetadataTagged>();
            var itemsToDo = new List<TimedItemWithMetadataTagged>();
            var itemsAlreadyDone = new List<TimedItemWithMetadataTagged>();
            var itemsForTraining = new List<TimedItemWithMetadataTagged>();

            foreach (var referenceTiming in r_referenceTimings)
            {
                var workingOnItems = r_workingOnContainer.GetItems().Where(f => f.StartTime == referenceTiming.StartTime);
                var workingOnItem = workingOnItems.LastOrDefault(); // Should we take the last??

                var item = new TimedItemWithMetadataTagged(referenceTiming, workingOnItem?.Metadata);
                allItems.Add(item);

                var isItemNeeded = r_metadataNeededRules == null ? true : IsRulesRespected(r_metadataNeededRules, referenceTiming.Metadata);
                var isItemAlreadyDone = workingOnItem != null && IsRulesRespected(r_metadataProducedRule, workingOnItem.Metadata);

                if (isItemNeeded && isItemAlreadyDone)
                {
                    itemsAlreadyDone.Add(item);
                }

                if (isItemNeeded && !isItemAlreadyDone)
                {
                    itemsToDo.Add(item);
                }
            }

            return (allItems.ToArray(), itemsToDo.ToArray(), itemsAlreadyDone.ToArray(), itemsForTraining.ToArray());
        }

        internal ITiming[] GetTimings()
        {
            var (allItems, _, _, _) = this.AnalyzeItemsState();
            return allItems.Cast<ITiming>().ToArray();
        }

        public AIRequest[] CreateNextRequests(
            SubtitleGeneratorContext context,
            ref int requestNumber,
            AIResponse[] lastResponsesReceived,
            BinaryDataExtractorCachedCollection binaryDataExtractors = null)
        {
            var lastResponse = lastResponsesReceived?.LastOrDefault();

            var (allItems, itemsToDo, itemsAlreadyDone, itemsForTraining) = this.AnalyzeItemsState();

            if (itemsToDo.Length == 0)
                return Array.Empty<AIRequest>();

            // Check that every individual request in the previous execution met NbItemsMinimumReceivedToContinue
            // Check that the aggregated responses from the previous execution met the minimum required items
            if (lastResponsesReceived != null && lastResponsesReceived.Length > 0)
            {
                int totalItemsIncluded = 0;
                int totalRequiredMinimum = 0;
                int totalItemsDone = 0;

                foreach (var response in lastResponsesReceived)
                {
                    var itemsIncludedCount = response.Request.ItemsIncluded?.Length ?? 0;
                    totalItemsIncluded += itemsIncludedCount;
                    totalRequiredMinimum += Math.Min(itemsIncludedCount, r_options.NbItemsMinimumReceivedToContinue);
                    // Count how many items from this request were successfully processed
                    totalItemsDone += response.Request.ItemsIncluded.Count(
                        item => itemsAlreadyDone.Any(f => f.StartTime == item.StartTime && f.EndTime == item.EndTime));
                }

                // Evaluate the aggregate totals
                if (totalItemsDone < totalRequiredMinimum)
                {
                    context.WriteError($"The last execution only returned {totalItemsDone}/{totalItemsIncluded} items across {lastResponsesReceived.Length} requests, when the aggregated minimum to continue is {totalRequiredMinimum}.");
                    return Array.Empty<AIRequest>();
                }
            }

            var requests = new List<AIRequest>();
            var scheduledInThisPass = new HashSet<TimedItemWithMetadataTagged>();
            int totalItemsCount = itemsAlreadyDone.Length + itemsToDo.Length;

            do
            {
                var remainingToDo = itemsToDo.Where(i => !scheduledInThisPass.Contains(i)).ToArray();
                if (remainingToDo.Length == 0)
                    break;

                var systemParts = new AIRequestPartCollection();
                var userParts = new AIRequestPartCollection();
                if (r_systemPrompt != null)
                {
                    systemParts.AddText(AIRequestSection.SystemPrompt, r_systemPrompt);
                }

                if (r_userPrompt != null)
                {
                    userParts.AddText(AIRequestSection.SystemValidation, r_userPrompt);
                }

                userParts.AddRange(binaryDataExtractors?.GetTrainingContentList() ?? Array.Empty<AIRequestPart>());

                var optimalBatchSize = r_options.BatchSize;

                // 1. Try to find an optimal split point by looking BACKWARD (shrinking the batch)
                if (remainingToDo.Length > optimalBatchSize && r_options.BatchSplitWindows > 0)
                {
                    int searchStartIndex = Math.Max(0, optimalBatchSize - r_options.BatchSplitWindows);
                    int searchEndIndex = Math.Min(optimalBatchSize, remainingToDo.Length - 1);
                    var largestGap = TimeSpan.MinValue;
                    for (int i = searchStartIndex; i < searchEndIndex; i++)
                    {
                        var gap = remainingToDo[i + 1].StartTime - remainingToDo[i].EndTime;
                        if (gap > largestGap)
                        {
                            largestGap = gap;
                            optimalBatchSize = i + 1;
                        }
                    }
                }

                // 2. Ensure we don't cut during overlapping/tight dialogue by looking FORWARD (growing the batch)
                if (r_options.BatchKeepTogetherThreshold.HasValue && remainingToDo.Length > optimalBatchSize)
                {
                    while (optimalBatchSize < remainingToDo.Length)
                    {
                        var currentItem = remainingToDo[optimalBatchSize - 1];
                        var nextItem = remainingToDo[optimalBatchSize];

                        var gapToNext = nextItem.StartTime - currentItem.EndTime;

                        if (gapToNext < r_options.BatchKeepTogetherThreshold.Value)
                        {
                            optimalBatchSize++;
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                var waitingForFirstToDo = true;
                var itemsInBatch = new List<TimedItemWithMetadata>();
                var metadataOngoing = new MetadataCollection();
                var contentBefore = new Queue<TimedItemWithMetadata>();
                var previousEndTime = TimeSpan.Zero;
                var nbImagesInBatch = 0;
                var audioSecondsInBatch = 0.0;

                foreach (var item in allItems)
                {
                    bool isItemToDoNow = remainingToDo.Contains(item);

                    if (waitingForFirstToDo)
                    {
                        if (!isItemToDoNow)
                        {
                            if (r_options.NbContextItems != null && r_options.NbContextItems > 0)
                            {
                                contentBefore.Enqueue(item);
                                if (contentBefore.Count > r_options.NbContextItems)
                                {
                                    contentBefore.Dequeue();
                                }
                            }

                            metadataOngoing.Merge(item.Metadata);
                        }
                        else
                        {
                            waitingForFirstToDo = false;
                            if (contentBefore.Count > 0)
                            {
                                userParts.AddText(AIRequestSection.ContextNodes, $"{r_options.TextBeforeContextData}\n[\n");

                                for (var index = 0; index < contentBefore.Count; index++)
                                {
                                    var current = contentBefore.ElementAt(index);
                                    var contextNumber = contentBefore.Count - index;
                                    var overrides = (index == 0) ? AddOngoingMetadata(current.Metadata, metadataOngoing) : null;
                                    metadataOngoing = null;
                                    userParts.AddRange(
                                        CreateNodeContents(
                                            AIRequestSection.ContextNodes,
                                            current,
                                            overrides: overrides,
                                            contextNumber: contextNumber));
                                }
                                userParts.AddText(AIRequestSection.ContextNodes, $"]\n{r_options.TextAfterContextData}\n");
                            }

                            userParts.AddText(AIRequestSection.ContextNodes, r_options.TextBeforeAnalysis + "\n\n[\n");
                        }
                    }

                    if (!waitingForFirstToDo)
                    {
                        if (isItemToDoNow)
                        {
                            var metadataForThisItem = item.Metadata;
                            if (metadataOngoing != null)
                            {
                                metadataForThisItem = AddOngoingMetadata(item.Metadata, metadataOngoing);
                                metadataOngoing = null;
                            }

                            var itemParts = new List<AIRequestPart>();
                            itemParts.AddRange(
                                CreateContextOnlyNodeContents(
                                    AIRequestSection.ContextNodes,
                                    binaryDataExtractors,
                                    new Timing(previousEndTime, item.StartTime),
                                    item.Metadata));
                            itemParts.AddRange(
                                CreateNodeContents(
                                    AIRequestSection.PrimaryNodes,
                                    item,
                                    binaryDataExtractors,
                                    metadataForThisItem));

                            nbImagesInBatch += itemParts.OfType<AIRequestPartImage>().Count();
                            audioSecondsInBatch += itemParts.OfType<AIRequestPartAudio>().Sum(a => a.Duration.TotalSeconds);
                            // Always include at least the minimum items received to continue.
                            if (itemsInBatch.Count > r_options.NbItemsMinimumReceivedToContinue
                                && ((r_options.BatchMaxImages.HasValue && nbImagesInBatch > r_options.BatchMaxImages.Value)
                                    || (r_options.BatchMaxAudioDuration.HasValue && audioSecondsInBatch > r_options.BatchMaxAudioDuration.Value.TotalSeconds)))
                            {
                                break;
                            }

                            userParts.AddRange(itemParts);
                            itemsInBatch.Add(item);
                            scheduledInThisPass.Add(item);
                        }
                    }

                    previousEndTime = item.EndTime;

                    if (itemsInBatch.Count >= optimalBatchSize)
                    {
                        break;
                    }
                }

                userParts.AddText(AIRequestSection.PrimaryNodes, "]\n");

                if (r_options.TextAfterAnalysis != null)
                {
                    userParts.AddText(AIRequestSection.PrimaryNodes, r_options.TextAfterAnalysis);
                }

                int startItemNumber = itemsAlreadyDone.Length + scheduledInThisPass.Count - itemsInBatch.Count + 1;
                int endItemNumber = itemsAlreadyDone.Length + scheduledInThisPass.Count;

                requests.Add(new AIRequest(
                    r_processStartTime,
                    requestNumber++,
                    r_workingOnContainer.Id,
                    itemsInBatch.ToArray(),
                    systemParts,
                    userParts,
                    r_options.MetadataAlwaysProduced,
                    $"Items {startItemNumber} to {endItemNumber} out of {totalItemsCount}"));

            } while (requests.Count < r_options.MaximumRequestsInBatch);

            return requests.ToArray();
        }

        private static MetadataCollection AddOngoingMetadata(MetadataCollection metadataForItem, MetadataCollection metadataOngoing)
        {
            if (metadataOngoing == null)
                return metadataForItem;

            foreach (var kvp in metadataOngoing.ToArray())
            {
                if (!kvp.Key.StartsWith("Ongoing", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(kvp.Value))
                {
                    metadataOngoing.Remove(kvp.Key);
                }
            }

            metadataOngoing.Merge(metadataForItem);
            return metadataOngoing;
        }

        private IEnumerable<AIRequestPart> CreateNodeContents(
            AIRequestSection section,
            TimedItemWithMetadata item,
            BinaryDataExtractorCachedCollection binaryDataExtractors = null,
            MetadataCollection overrides = null,
            int? contextNumber = null)
        {
            bool ShouldBeIncluded(string metadataName, int? contextNumber)
            {
                if (contextNumber == null)
                {
                    return true;
                }
                return (r_options.MetadataInContextLimits?.TryGetValue(metadataName, out var limit) == true)
                    ? (contextNumber <= limit)
                    : true;
            }

            var parts = new AIRequestPartCollection();
            var sb = new StringBuilder();
            sb.AppendLine("  {");
            if (ShouldBeIncluded("StartTime", contextNumber))
            {
                sb.AppendLine($"    \"StartTime\": \"{item.StartTime:hh\\:mm\\:ss\\.fff}\",");
            }
            if (ShouldBeIncluded("EndTime", contextNumber) && (r_options.FieldsToInclude & NodeFields.EndTime) != 0)
            {
                sb.AppendLine($"    \"EndTime\": \"{item.EndTime:hh\\:mm\\:ss\\.fff}\",");
            }
            if (ShouldBeIncluded("Duration", contextNumber) && (r_options.FieldsToInclude & NodeFields.Duration) != 0)
            {
                sb.AppendLine($"    \"Duration\": \"{item.Duration:hh\\:mm\\:ss\\.fff}\",");
            }
            foreach (var metadata in new MetadataCollection(overrides ?? item.Metadata))
            {
                if (ShouldBeIncluded(metadata.Key, contextNumber))
                {
                    sb.AppendLine($"    {JsonConvert.ToString(metadata.Key)}: {JsonConvert.ToString(metadata.Value)},");
                }
            }
            if (binaryDataExtractors != null)
            {
                parts.AddText(section, sb.ToString());
                sb.Clear();
                foreach (var binaryItem in binaryDataExtractors.GetNamedContentListForItem(section, item, item.StartTime.ToString(@"hh\:mm\:ss\.fff")))
                {
                    sb.Append($"    \"{binaryItem.Key}\": ");
                    parts.AddText(section, sb.ToString(), binaryItem.Value.FirstOrDefault()?.AssociatedDataType ?? BinaryDataType.Image);
                    sb.Clear();
                    parts.AddRange(binaryItem.Value);
                    sb.AppendLine();
                }
            }
            sb.AppendLine("  },");
            parts.AddText(section, sb.ToString());
            sb.Clear();

            return parts;
        }

        private static IEnumerable<AIRequestPart> CreateContextOnlyNodeContents(
            AIRequestSection section,
            BinaryDataExtractorCachedCollection extractors,
            ITiming timing,
            MetadataCollection metadatas)
        {
            if (extractors == null)
            {
                yield break;
            }

            foreach (var node in extractors
                .GetContextOnlyNodes(section, timing, metadatas, (t) => t.ToString(@"hh\:mm\:ss\.fff"))
                .OrderBy(n => n.time))
            {
                var sb = new StringBuilder();
                sb.AppendLine("  {");
                sb.AppendLine($"    \"StartTime FOR CONTEXT ONLY\": \"{node.time:hh\\:mm\\:ss\\.fff}\",");
                BinaryDataType? lastDataType = null;
                foreach (var binaryItem in node.binaryItems)
                {
                    sb.Append($"    \"{binaryItem.name}\": ");
                    yield return new AIRequestPartText(section, sb.ToString(), binaryItem.dataType);
                    sb.Clear();
                    foreach (var cl in binaryItem.contentList)
                    {
                        yield return cl;
                    }
                    sb.AppendLine();
                    yield return new AIRequestPartText(section, sb.ToString(), binaryItem.dataType);
                    sb.Clear();

                    lastDataType = binaryItem.dataType;
                }
                sb.AppendLine("  },");
                yield return new AIRequestPartText(section, sb.ToString(), lastDataType);
            }
        }

        internal AIRequest CreateEmptyRequest()
        {
            return new AIRequest(
                r_processStartTime,
                1,
                r_workingOnContainer.Id,
                null,
                null,
                null,
                r_options.MetadataAlwaysProduced,
                "");
        }

        internal bool IsFinished()
        {
            var (_, itemsToDo, _, _) = AnalyzeItemsState();
            return itemsToDo.Length == 0;
        }

        private static bool IsRulesRespected(string[] rules, MetadataCollection metadatas, MetadataCollection otherMetadatas = null)
        {
            if (rules == null || rules.Length == 0)
            {
                return false;
            }
            if ((otherMetadatas != null) && IsRulesRespected(rules, otherMetadatas) == false)
            {
                return false;
            }
            foreach (var rule in rules)
            {
                var isNegative = rule.StartsWith("!");
                var nameOrRegex = isNegative ? rule.Substring(1) : rule;
                if (isNegative)
                {
                    if (metadatas.Get(nameOrRegex) != null)
                        return false;
                }
                else
                {
                    var regex = GetRegex(nameOrRegex);
                    if (!metadatas.Any(m => regex.IsMatch(m.Key) && metadatas.Values != null))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static readonly Dictionary<string, Regex> rs_cachedRegex = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);

        private static Regex GetRegex(string pattern)
        {
            if (!rs_cachedRegex.TryGetValue(pattern, out var regex))
            {
                regex = new Regex(pattern, RegexOptions.IgnoreCase);
                rs_cachedRegex.Add(pattern, regex);
            }
            return regex;
        }
    }
}