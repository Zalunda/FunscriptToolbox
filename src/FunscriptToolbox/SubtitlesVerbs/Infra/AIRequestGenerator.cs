using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIRequestGenerator
    {
        private readonly TimedItemWithMetadata[] r_referenceTimings;
        private readonly TimedItemWithMetadataCollection r_workingOnContainer;
        private readonly string r_systemPrompt;
        private readonly string r_firstUserPrompt;
        private readonly string r_otherUserPrompt;
        private readonly string[] r_metadataNeededRules;
        private readonly string[] r_metadataProducedRule;
        private readonly string[] r_metadataForTrainingRules;
        private readonly AIOptions r_options;

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
            r_firstUserPrompt = options.FirstUserPrompt?.GetFinalText(transcriptionLanguage, translationLanguage);
            r_otherUserPrompt = options.OtherUserPrompt?.GetFinalText(transcriptionLanguage, translationLanguage);
            r_metadataNeededRules = options.MetadataNeeded?.Split(',').Select(f => f.Trim()).ToArray();
            r_metadataProducedRule = options.MetadataAlwaysProduced?.Split(',').Select(f => f.Trim()).ToArray();
            r_metadataForTrainingRules = options.MetadataForTraining?.Split(',').Select(f => f.Trim()).ToArray();

            r_options = options;
        }

        public (TimedItemWithMetadataTagged[] allItems, TimedItemWithMetadataTagged[] itemsToDo, TimedItemWithMetadataTagged[]itemAlreadyDone, TimedItemWithMetadataTagged[] itemsForTraining) AnalyzeItemsState()
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
                var isItemAlreadyDone = (workingOnItem != null && IsRulesRespected(r_metadataProducedRule, workingOnItem.Metadata));

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
            AIRequest lastRequestExecuted,
            CachedBinaryGenerator binaryGenerator = null)
        {
            var (allItems, itemsToDo, itemsAlreadyDone, itemsForTraining) = this.AnalyzeItemsState();

            if (itemsToDo.Length == 0)
                return null;

            var nbItemsInLastResponse = lastRequestExecuted?.NbItemsToDoTotal - itemsToDo.Length;
            if (nbItemsInLastResponse < r_options.BatchSize && nbItemsInLastResponse < r_options.NbItemsMinimumReceivedToContinue)
            {
                context.WriteError($"Last response only contained {nbItemsInLastResponse} items when minimum to continue is {r_options.NbItemsMinimumReceivedToContinue}.");
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

            if (r_firstUserPrompt != null)
            {
                contentList.Add(new
                {
                    type = "text",
                    text = r_firstUserPrompt
                });
            }

            bool needSeparatorLine = false;
            var separatorLine = new string('-', 20) + "\n";

            if (itemsForTraining.Length > 0)
            {
                contentList.Add(new
                {
                    type = "text",
                    text = $"{(needSeparatorLine ? separatorLine : string.Empty)}{r_options.TextBeforeTrainingData}"
                });

                foreach (var item in itemsForTraining.Take(r_options.NbItemsMaximumForTraining))
                {
                    contentList.Add(new
                    {
                        type = "text",
                        text = item.Metadata.Get(r_metadataForTrainingRules.First())
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
                        text = r_options.TextAfterTrainingData
                    });
                }

                needSeparatorLine = true;
            }

            var waitingForFirstToDo = true;
            var nbItemsInBatch = 0;
            int nbExtraContextItems = 0;
            var metadataOngoing = new MetadataCollection();
            var contentBefore = new Queue<TimedItemWithMetadata>();
            var contentExtraContext = new List<dynamic>();
            foreach (var item in allItems)
            {
                dynamic CreateMetadataContent(TimedItemWithMetadata item, MetadataCollection overrides = null, string prefix = "") => new
                {
                    type = "text",
                    text = prefix + JsonConvert.SerializeObject(
                        new MetadataCollection(overrides ?? item.Metadata)
                        {
                        { "StartTime", item.StartTime.ToString(@"hh\:mm\:ss\.fff") },
                        { "EndTime", item.EndTime.ToString(@"hh\:mm\:ss\.fff") }
                        },
                        Formatting.Indented)
                };
                MetadataCollection AddOngoingMetadata(MetadataCollection metadataForItem, MetadataCollection metadataOngoing)
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
                                text = $"{(needSeparatorLine ? separatorLine : string.Empty)}{r_options.TextBeforeContextData}"
                            });

                            var firstContent = contentBefore.Dequeue();
                            contentList.Add(
                                CreateMetadataContent(
                                    firstContent,
                                    AddOngoingMetadata(firstContent.Metadata, metadataOngoing)));
                            metadataOngoing = null;

                            contentList.AddRange(
                                contentBefore.Select(c => CreateMetadataContent(c)));

                            if (r_options.TextAfterContextData != null)
                            {
                                contentList.Add(new
                                {
                                    type = "text",
                                    text = r_options.TextAfterContextData
                                });
                            }
                            needSeparatorLine = true;
                        }

                        if (r_options.TextBeforeAnalysis != null)
                        {
                            contentList.Add(new
                            {
                                type = "text",
                                text = r_options.TextBeforeAnalysis
                            });
                        }
                    }
                }

                if (!waitingForFirstToDo)
                {
                    if (binaryGenerator != null)
                    {
                        needSeparatorLine = true;
                    }

                    if (itemsToDo.Contains(item))
                    {
                        contentList.AddRange(contentExtraContext);
                        contentExtraContext.Clear();

                        if (metadataOngoing != null)
                        {
                            contentList.Add(
                                CreateMetadataContent(
                                    item,
                                    AddOngoingMetadata(item.Metadata, metadataOngoing),
                                    (needSeparatorLine ? separatorLine : string.Empty)));
                            metadataOngoing = null;
                        }
                        else
                        {
                            contentList.Add(
                                CreateMetadataContent(
                                    item, 
                                    prefix: (needSeparatorLine ? separatorLine : string.Empty)));
                        }

                        if (binaryGenerator != null)
                        {
                            contentList.AddRange(binaryGenerator.GetBinaryContent(item));
                        }
                        nbItemsInBatch++;

                        if (nbItemsInBatch >= r_options.BatchSize)
                        {
                            break;
                        }
                    }
                    else if (r_options.NbContextItems != null)
                    {
                        contentExtraContext.Add(
                            CreateMetadataContent(item,
                            prefix: (needSeparatorLine ? separatorLine : string.Empty)));
                        nbExtraContextItems++;

                        if ((nbItemsInBatch > 0) && (contentBefore.Count + nbExtraContextItems >= r_options.NbContextItems))
                        {
                            break;
                        }
                    }
                }
            }

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
                requestNumber,
                r_workingOnContainer.Id,
                messages,
                itemsToDo.Length,
                r_options.MetadataAlwaysProduced);
        }

        internal AIRequest CreateEmptyRequest()
        {
            return new AIRequest(
                1, 
                r_workingOnContainer.Id,
                null,
                0,
                r_options.MetadataAlwaysProduced);
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