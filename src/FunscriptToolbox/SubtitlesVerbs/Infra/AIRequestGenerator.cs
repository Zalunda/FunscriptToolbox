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
        private readonly string[] r_metadataForTrainingRules;
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
            r_metadataForTrainingRules = options.MetadataForTraining?.Split(',').Select(f => f.Trim()).ToArray();

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

                if (IsRulesRespected(r_metadataForTrainingRules, referenceTiming.Metadata))
                {
                    itemsForTraining.Add(item);
                }

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
            CachedBinaryGenerator binaryGenerator = null)
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

            var contentList = new List<dynamic>();
            var messages = new List<dynamic>();
            if (r_systemPrompt != null)
            {
                messages.Add(new
                {
                    role = "system",
                    content = r_systemPrompt
                });
            }

            if (r_userPrompt != null)
            {
                contentList.Add(new
                {
                    type = "text",
                    text = r_userPrompt
                });
            }

            if (itemsForTraining.Length > 0)
            {
                if (r_options.TextBeforeTrainingData != null)
                {
                    contentList.Add(new
                    {
                        type = "text",
                        text = r_options.TextBeforeTrainingData + "\n"
                    });
                }

                foreach (var item in itemsForTraining.Take(r_options.NbItemsMaximumForTraining))
                {
                    contentList.Add(new
                    {
                        type = "text",
                        text = item.Metadata.Get(r_metadataForTrainingRules.First()) + "\n"
                    });
                    if (binaryGenerator != null)
                    {
                        contentList.AddRange(binaryGenerator.GetBinaryContent(item));
                    }
                }
                if (r_options.TextAfterTrainingData != null)
                {
                    contentList.Add(new
                    {
                        type = "text",
                        text = r_options.TextAfterTrainingData + "\n"
                    });
                }
            }

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
            var contentExtraContext = new List<dynamic>();
            foreach (var item in allItems)
            {
                IEnumerable<dynamic> CreateNodeContents(TimedItemWithMetadata item, CachedBinaryGenerator binaryGenerator = null, MetadataCollection overrides = null)
                {
                    var nodeContents = new List<dynamic>();
                    var sb = new StringBuilder();
                    sb.AppendLine("  {");
                    sb.AppendLine($"    \"StartTime\": \"{item.StartTime:hh\\:mm\\:ss\\.fff}\",");
                    if ((r_options.FieldsToInclude & NodeFields.EndTime) != 0)
                    {
                        sb.AppendLine($"    \"EndTime\": \"{item.EndTime:hh\\:mm\\:ss\\.fff}\",");
                    }
                    if ((r_options.FieldsToInclude & NodeFields.Duration) != 0)
                    {
                        sb.AppendLine($"    \"Duration\": \"{item.Duration:hh\\:mm\\:ss\\.fff}\",");
                    }
                    var fullMetadata = new MetadataCollection(overrides ?? item.Metadata);
                    foreach (var metadata in new MetadataCollection(overrides ?? item.Metadata))
                    {
                        sb.AppendLine($"    {JsonConvert.ToString(metadata.Key)}: {JsonConvert.ToString(metadata.Value)},");
                    }
                    if (binaryGenerator != null)
                    {
                        var (data, type) = binaryGenerator.GetBinaryContentWithType(item, item.StartTime.ToString(@"hh\:mm\:ss\.fff"));

                        sb.Append($"    \"{type}\": ");
                        nodeContents.Add(new
                        {
                            type = "text",
                            text = sb.ToString()
                        });
                        sb.Clear();

                        nodeContents.AddRange(data);
                        sb.AppendLine();
                    }
                    sb.AppendLine("  },");
                    nodeContents.Add(new
                    {
                        type = "text",
                        text = sb.ToString()
                    });
                    sb.Clear();

                    return nodeContents;
                }
                static MetadataCollection AddOngoingMetadata(MetadataCollection metadataForItem, MetadataCollection metadataOngoing)
                {
                    if (metadataOngoing == null)
                        return metadataForItem;

                    foreach (var key in metadataOngoing.Keys.ToArray())
                    {
                        if (!key.StartsWith("Ongoing", StringComparison.OrdinalIgnoreCase))
                        {
                            metadataOngoing.Remove(key);
                        }
                    }

                    metadataOngoing.Merge(metadataForItem);
                    return metadataOngoing;
                }

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
                            contentList.Add(new
                            {
                                type = "text",
                                text = $"{r_options.TextBeforeContextData}\n[\n"
                            });

                            var firstContent = contentBefore.Dequeue();
                            contentList.AddRange(
                                CreateNodeContents(
                                    firstContent,
                                    overrides: AddOngoingMetadata(firstContent.Metadata, metadataOngoing)));
                            metadataOngoing = null;

                            contentList.AddRange(
                                contentBefore.SelectMany(c => CreateNodeContents(c)));
                            contentList.Add(new
                            {
                                type = "text",
                                text = "]\n"
                            });

                            contentList.Add(new
                            {
                                type = "text",
                                text = r_options.TextAfterContextData + "\n"
                            });
                        }

                        contentList.Add(new
                        {
                            type = "text",
                            text = r_options.TextBeforeAnalysis + "\n\n[\n"
                        });
                    }
                }

                if (!waitingForFirstToDo)
                {
                    if (itemsToDo.Contains(item))
                    {
                        contentList.AddRange(contentExtraContext);
                        contentExtraContext.Clear();

                        var metadataForThisItem = item.Metadata;

                        if (metadataOngoing != null)
                        {
                            metadataForThisItem = AddOngoingMetadata(item.Metadata, metadataOngoing);
                            metadataOngoing = null;
                        }

                        contentList.AddRange(CreateNodeContents(
                            item,
                            binaryGenerator,
                            metadataForThisItem));
                        itemsInBatch.Add(item);

                        if (itemsInBatch.Count >= optimalBatchSize)
                        {
                            break;
                        }
                    }
                }
            }
            contentList.Add(new
            {
                type = "text",
                text = "]\n"
            });

            if (r_options.TextAfterAnalysis != null)
            {
                contentList.Add(new
                {
                    type = "text",
                    text = r_options.TextAfterAnalysis
                });
            }

            messages.Add(new
            {
                role = "user",
                content = contentList.ToArray()
            });

            return new AIRequest(
                r_processStartTime,
                requestNumber,
                r_workingOnContainer.Id,
                itemsInBatch.ToArray(),
                messages,
                r_options.MetadataAlwaysProduced,
                $"Items {itemsAlreadyDone.Length + 1} to {itemsAlreadyDone.Length + itemsInBatch.Count} out of {itemsAlreadyDone.Length + itemsToDo.Length}");
        }

        internal AIRequest CreateEmptyRequest()
        {
            return new AIRequest(
                r_processStartTime,
                1, 
                r_workingOnContainer.Id,
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