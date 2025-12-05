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

            r_referenceTimings = referenceTimings ?? Array.Empty<TimedItemWithMetadata>();
            r_workingOnContainer = workingOnContainer;

            r_systemPrompt = options.SystemPrompt?.GetFinalText(transcriptionLanguage, translationLanguage);
            r_userPrompt = options.UserPrompt?.GetFinalText(transcriptionLanguage, translationLanguage);
            r_metadataNeededRules = options.MetadataNeeded?.Split(',').Select(f => f.Trim()).ToArray();
            r_metadataProducedRule = options.MetadataAlwaysProduced?.Split(',').Select(f => f.Trim()).ToArray();

            r_options = options;
            r_processStartTime = DateTime.Now;
        }

        public (TimedItemWithMetadataTagged[] allItems, TimedItemWithMetadataTagged[] itemsToDo, TimedItemWithMetadataTagged[]itemAlreadyDone, TimedItemWithMetadataTagged[] itemsForTraining) AnalyzeItemsState(
            TimeSpan? assumeAllItemsFinishedBefore = null)
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
                var isItemAlreadyDone = (workingOnItem != null && IsRulesRespected(r_metadataProducedRule, workingOnItem.Metadata))
                    || (referenceTiming.StartTime < assumeAllItemsFinishedBefore);

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

        public AIRequest CreateNextRequest(
            SubtitleGeneratorContext context,
            int requestNumber,
            AIResponse lastResponseReceived,
            BinaryDataExtractorCachedCollection binaryDataExtractors = null)
        {
            // If no assistant message => Chatbot => assume all items were processed
            var assumeAllItemsFinishedBefore = (lastResponseReceived?.AssistantMessage == null)
                ? lastResponseReceived?.Request.ItemsIncluded.Last().EndTime
                : null;

            var (allItems, itemsToDo, itemsAlreadyDone, itemsForTraining) = this.AnalyzeItemsState(assumeAllItemsFinishedBefore);

            if (itemsToDo.Length == 0)
                return null;

            var nbItemsDoneInLastRequest = lastResponseReceived?.Request.ItemsIncluded.Count(item => itemsAlreadyDone.Any(f => f.StartTime == item.StartTime && f.EndTime == item.EndTime));
            if (nbItemsDoneInLastRequest < r_options.NbItemsMinimumReceivedToContinue)
            {
                context.WriteError($"Last response only contained {nbItemsDoneInLastRequest} items when minimum to continue is {r_options.NbItemsMinimumReceivedToContinue}.");
                return null;
            }

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
            if (itemsToDo.Length > optimalBatchSize && r_options.BatchSplitWindows > 0)
            {
                // Define the window to search for the optimal split point.
                // Example: BatchSize=1000, Window=20 -> Search starts at index 980.
                int searchStartIndex = Math.Max(0, optimalBatchSize - r_options.BatchSplitWindows);

                // We search for a gap up to the end of the ideal batch size.
                // The loop limit must be one less than the item we are accessing.
                int searchEndIndex = Math.Min(optimalBatchSize, itemsToDo.Length - 1);

                // Loop from the start of the window up to the second-to-last item in the search range.
                var largestGap = TimeSpan.MinValue;
                for (int i = searchStartIndex; i < searchEndIndex; i++)
                {
                    // Calculate the gap between the current item and the next one.
                    var gap = itemsToDo[i + 1].StartTime - itemsToDo[i].EndTime;

                    if (gap > largestGap)
                    {
                        largestGap = gap;
                        // The new batch size should be i + 1, as we want to include item 'i'.
                        optimalBatchSize = i + 1;
                    }
                }
            }

            var waitingForFirstToDo = true;
            var itemsInBatch = new List<TimedItemWithMetadata>();
            var metadataOngoing = new MetadataCollection();
            var contentBefore = new Queue<TimedItemWithMetadata>();
            var previousEndTime = TimeSpan.Zero;
            foreach (var item in allItems)
            {
                if (waitingForFirstToDo)
                {
                    if (!itemsToDo.Contains(item))
                    {
                        if (r_options.NbContextItems != null)
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
                    if (itemsToDo.Contains(item))
                    {
                        var metadataForThisItem = item.Metadata;
                        if (metadataOngoing != null)
                        {
                            metadataForThisItem = AddOngoingMetadata(item.Metadata, metadataOngoing);
                            metadataOngoing = null;
                        }

                        userParts.AddRange(
                            CreateContextOnlyNodeContents(
                                AIRequestSection.ContextNodes,
                                binaryDataExtractors,                                 
                                new Timing(previousEndTime, item.StartTime),
                                item.Metadata));
                        userParts.AddRange(
                            CreateNodeContents(
                                AIRequestSection.PrimaryNodes,
                                item,
                                binaryDataExtractors,
                                metadataForThisItem));
                        itemsInBatch.Add(item);
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

            return new AIRequest(
                r_processStartTime,
                requestNumber,
                r_workingOnContainer.Id,
                itemsInBatch.ToArray(),
                systemParts,
                userParts,
                r_options.MetadataAlwaysProduced,
                $"Items {itemsAlreadyDone.Length + 1} to {itemsAlreadyDone.Length + itemsInBatch.Count} out of {itemsAlreadyDone.Length + itemsToDo.Length}");
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
                foreach (var binaryItem in binaryDataExtractors.GetNamedContentListForItem(section, item, item.StartTime.ToString(@"hh\:mm\:ss\.fff")))
                {
                    sb.Append($"    \"{binaryItem.Key}\": ");
                    parts.AddText(section, sb.ToString());
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
                foreach (var binaryItem in node.binaryItems)
                {
                    sb.Append($"    \"{binaryItem.name}\": ");
                    yield return new AIRequestPartText(section, sb.ToString());
                    sb.Clear();
                    foreach (var cl in binaryItem.contentList)
                    {
                        yield return cl;
                    }
                    sb.AppendLine();
                }
                sb.AppendLine("  },");
                yield return new AIRequestPartText(section, sb.ToString());
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