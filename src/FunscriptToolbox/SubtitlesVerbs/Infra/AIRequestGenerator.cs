using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
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
        private readonly bool r_sendAllItemsToAI;
        private readonly int r_batchSize;
        public int MinimumItemsAddedToContinue { get; }

        public AIRequestGenerator(
            TimedItemWithMetadata[] referenceTimings, 
            Transcription transcription,
            Language translationLanguage,
            AIOptions options = null)
        {
            options = options ?? new AIOptions();

            r_referenceTimings = referenceTimings;
            r_workingOnContainer = transcription;

            r_systemPrompt = options.SystemPrompt?.GetFinalText(transcription.Language, translationLanguage);
            r_firstUserPrompt = options.FirstUserPrompt?.GetFinalText(transcription.Language, translationLanguage);
            r_otherUserPrompt = options.OtherUserPrompt?.GetFinalText(transcription.Language, translationLanguage);
            r_metadataNeededRules = options.MetadataNeeded?.Split(',').Select(f => f.Trim()).ToArray();
            r_metadataProducedRule = options.MetadataProduced?.Split(',').Select(f => f.Trim()).ToArray();
            r_metadataForTrainingRules = options.MetadataForTraining?.Split(',').Select(f => f.Trim()).ToArray();
            r_sendAllItemsToAI = options.SendAllItemsToAI;

            r_batchSize = options.BatchSize;
            this.MinimumItemsAddedToContinue = options.MinimumItemsAddedToContinue;
        }

        public (TimedItemWithMetadataTagged[], TimedItemWithMetadataTagged[], TimedItemWithMetadataTagged[], TimedItemWithMetadataTagged[]) AnalyzeItemsState()
        {
            var itemsToDo = new List<TimedItemWithMetadataTagged>();
            var itemsAlreadyDone = new List<TimedItemWithMetadataTagged>();
            var itemsToSendToAI = new List<TimedItemWithMetadataTagged>();
            var itemsForTraining = new List<TimedItemWithMetadataTagged>();

            foreach (var referenceTiming in r_referenceTimings)
            {
                var workingOnItems = r_workingOnContainer.GetItems().Where(f => f.StartTime == referenceTiming.StartTime);
                if (workingOnItems.Count() > 1)
                {
                    throw new Exception("What should we do?");
                }
                var workingOnItem = workingOnItems.FirstOrDefault();

                var item = new TimedItemWithMetadataTagged(referenceTiming, workingOnItem?.Metadata);

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
                    itemsToSendToAI.Add(item);
                }
                else if (r_sendAllItemsToAI)
                {
                    itemsToSendToAI.Add(item);
                }
            }

            return (itemsToDo.ToArray(), itemsAlreadyDone.ToArray(), itemsToSendToAI.ToArray(), itemsForTraining.ToArray());
        }

        internal ITiming[] GetTimings()
        {
            var (itemsToDo, _, _, _) = this.AnalyzeItemsState();
            return itemsToDo.Cast<ITiming>().ToArray();
        }

        public AIRequest CreateNextRequest(
            int requestNumber,
            Dictionary<TimeSpan, dynamic[]> binaryContents = null)
        {
            var (itemsToDo, itemsAlreadyDone, allItems, itemsForTraining) = this.AnalyzeItemsState();

            if (itemsToDo.Length == 0)
                return null;

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
            if (itemsForTraining.Length > 0 && itemsAlreadyDone.Length > 0)
            {
                contentList.Add(new
                {
                    type = "text",
                    text = "Since this is a continuation request and the training data where in a previous part, here are a few segments for learning (person name followed by a segment to learn from):"
                });

                foreach (var item in itemsForTraining)
                {
                    contentList.Add(new
                    {
                        type = "text",
                        text = item.Metadata.Get(r_metadataForTrainingRules.First())
                    });
                    if (binaryContents?.TryGetValue(item.StartTime, out var data) == true)
                    {
                        contentList.AddRange(data);
                    }
                }
            }

            var nbItemsInBatch = 0;
            foreach (var item in allItems)
            {
                contentList.Add(new
                {
                    type = "text",
                    text = JsonConvert.SerializeObject(
                        new MetadataCollection(item.Metadata)
                        {
                            { "StartTime", item.StartTime.ToString(@"hh\:mm\:ss\.fff") },
                            { "EndTime", item.EndTime.ToString(@"hh\:mm\:ss\.fff") }
                        },
                        Formatting.Indented)
                });

                if (itemsToDo.Contains(item))
                {
                    if (binaryContents?.TryGetValue(item.StartTime, out var data) == true)
                    {
                        contentList.AddRange(data);
                    }

                    nbItemsInBatch++;
                    if (nbItemsInBatch == r_batchSize)
                        break;
                }
            }

            messages.Add(new
            {
                role = "user",
                content = contentList.ToArray()
            });

            return new AIRequest(
                requestNumber,
                r_workingOnContainer.FullId,
                messages,
                itemsToDo.Length);
        }

        internal bool IsFinished()
        {
            var (itemsToDo, _, _, _) = AnalyzeItemsState();
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