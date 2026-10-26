using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Text;
using FunscriptToolbox.Core.Infra;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIEngineRunner<T> : AIEngineRunner where T : TimedItemWithMetadata
    {
        private readonly SubtitleGeneratorContext r_context;
        private readonly AIEngine r_engine;
        private readonly AIEngineConfigOverride r_engineConfigOverride;
        private readonly TimedItemWithMetadataCollection<T> r_workingOnContainer;
        private readonly bool r_promptUserOnError;

        public AIEngineRunner(
            SubtitleGeneratorContext context,
            AIEngine engine,
            AIEngineConfigOverride engineConfigOverride,
            TimedItemWithMetadataCollection<T> workingOnContainer,
            bool promptUserOnError)
        {
            r_context = context;
            r_engine = engine;
            r_engineConfigOverride = engineConfigOverride;
            r_workingOnContainer = workingOnContainer;
            r_promptUserOnError = promptUserOnError;
        }

        public void Run(
            AIRequestGenerator requestsGenerator,
            BinaryDataExtractorCachedCollection binaryDataExtractors = null)
        {
            var nbErrors = HandlePreviousFiles(requestsGenerator);
            if (nbErrors == 0)
            {
                AIRequest[] requests = null;
                AIResponse[] lastResponsesReceived = null;
                int requestNumber = 1;
                do
                {
                    requests = requestsGenerator.CreateNextRequests(r_context, ref requestNumber, lastResponsesReceived, binaryDataExtractors);
                    if (requests != null && requests.Length > 0)
                    {
                        var watch = Stopwatch.StartNew();
                        var responses = r_engine.Execute(r_context, requests, r_engineConfigOverride);
                        watch.Stop();

                        lastResponsesReceived = responses;
                        var capturedExceptions = new List<AIResponseException>();

                        foreach (var response in responses)
                        {
                            r_workingOnContainer.Costs.Add(response.Cost);
                            r_workingOnContainer.Costs.AddRange(response.AdditionalCosts);

                            if (response.AssistantMessage != null)
                            {
                                try
                                {
                                    var itemsAdded = ParseAssistantMessageAndAddItems(
                                        requestsGenerator.GetTimings(),
                                        response.AssistantMessage,
                                        response.Request,
                                        () => response.Cost.NbItemsInResponse++);
                                    r_context.WIP.Save();
                                }
                                catch (AIResponseException ex)
                                {
                                    SaveErrorAndCreateTodo(ex);
                                    capturedExceptions.Add(ex);
                                }
                            }
                        }

                        if (capturedExceptions.Count > 0)
                        {
                            throw capturedExceptions[0];
                        }
                    }
                } while (requests != null && requests.Length > 0);
            }
        }

        private void SaveErrorAndCreateTodo(AIResponseException ex)
        {
            // ParseAssistantMessageAndAddItems might have added some of the items before throwing.
            r_context.WIP.Save();

            var todoFilepath = ex.Request.GetFilenamePattern(r_context.WIP.BaseFilePath);
            var errorFilePath = todoFilepath.Replace("TODO_", "TODO_E_");
            if (ex.ResponseBodyPartiallyFixed == null)
            {
                r_context.SoftDelete(errorFilePath);
                File.WriteAllText(
                    errorFilePath,
                    $"{ex.Message}\n\n{ex.ResponseBody}",
                    Encoding.UTF8);
            }
            else
            {
                r_context.SoftDelete(todoFilepath);
                File.WriteAllText(
                    todoFilepath,
                    $"{ex.Message.Replace("[", "(").Replace("]", ")")}\n\n{ex.ResponseBodyPartiallyFixed}",
                    Encoding.UTF8);
                r_context.AddUserTodo($"Manually fix the following error in file '{Path.GetFileName(todoFilepath)}':\n{ex.Message}");
            }
        }

        public int HandlePreviousFiles(AIRequestGenerator requestsGenerator)
        {
            var nbErrors = 0;
            var patternSuffix = "_\\d+\\.txt";

            foreach (var fullpath in Directory.GetFiles(
                PathExtension.SafeGetDirectoryName(r_context.WIP.BaseFilePath),
                "*.*"))
            {
                var filename = Path.GetFileName(fullpath);
                if (Regex.IsMatch(
                    filename,
                    $"^" + Regex.Escape($"{Path.GetFileName(r_context.WIP.BaseFilePath)}.TODO_{r_workingOnContainer.Id}") + $"{patternSuffix}$",
                    RegexOptions.IgnoreCase))
                {
                    var assistantMessageFromFile = File.ReadAllText(fullpath);
                    r_context.SoftDelete(fullpath);

                    try
                    {
                        r_context.WriteInfo($"        Analysing existing file '{filename}'...");
                        var itemsAdded = ParseAssistantMessageAndAddItems(requestsGenerator.GetTimings(), assistantMessageFromFile, requestsGenerator.CreateEmptyRequest());
                        r_context.WriteInfo($"        Finished:");
                        r_context.WriteInfo($"            Nb items added: {itemsAdded.Count}");
                        if (itemsAdded.Count > 0)
                        {
                            r_context.WIP.Save();
                        }
                    }
                    catch (AIResponseException ex)
                    {
                        nbErrors++;
                        File.WriteAllText(fullpath, $"{ex.Message.Replace("[", "(").Replace("]", ")")}\n\n{ex.ResponseBodyPartiallyFixed}", Encoding.UTF8);
                        r_context.WriteInfo($"Error while parsing file '{filename}':{ex.Message}");
                        r_context.AddUserTodo($"Manually fix the following error in file '{filename}':\n{ex.Message}");
                    }
                }
                else if (Regex.IsMatch(
                    filename,
                    $"^" + Regex.Escape($"{Path.GetFileName(r_context.WIP.BaseFilePath)}.TODO_E_{r_workingOnContainer.Id}") + $"{patternSuffix}$",
                    RegexOptions.IgnoreCase))
                {
                    // Soft delete old error file.
                    r_context.SoftDelete(fullpath);
                }
            }

            return nbErrors;
        }

        private class TranscriptionNode
        {
            public string StartTime { get; set; }
            public string EndTime { get; set; }
        }

        private List<T> ParseAssistantMessageAndAddItems(
            ITiming[] timings,
            string responseReceived,
            AIRequest request,
            Action increaseNbItemsInCostAction = null)
        {
            static string GetSegmentInformation(JToken segment)
            {
                var lineInfo = (IJsonLineInfo)segment;
                return (lineInfo != null && lineInfo.HasLineInfo())
                    ? $"Error in segment starting at line {lineInfo.LineNumber}, position {lineInfo.LinePosition}."
                    : $"Error in segment at unknown location.";
            }

            string textToParse = responseReceived;

            while (true)
            {
                string fixedJson = null;
                JToken currentSegment = null;
                try
                {
                    // Step 1: Get the cleaned-up JSON string from your fixing logic.
                    fixedJson = TryToFixReceivedJson(request, textToParse);

                    // Step 2: Parse the cleaned string using a reader to preserve line info.
                    JArray responseArray;
                    using (var stringReader = new StringReader(fixedJson))
                    using (var jsonReader = new JsonTextReader(stringReader))
                    {
                        // This will throw a detailed exception if the fixedJson is still invalid.
                        responseArray = JArray.Load(jsonReader);
                    }

                    // Temporary transactional list. Don't add to the main container until we know everything is valid.
                    var pendingItems = new List<(TimeSpan StartTime, TimeSpan EndTime, MetadataCollection ExtraMetadatas)>();

                    foreach (var segment in responseArray)
                    {
                        currentSegment = segment;
                        var node = segment.ToObject<TranscriptionNode>();

                        // Extract and remove known fields
                        if (string.IsNullOrWhiteSpace(node.StartTime))
                        {
                            throw new Exception($"StartTime is missing or empty. {GetSegmentInformation(segment)}");
                        }

                        var startTime = TimeSpanExtensions.FlexibleTimeSpanParse(node.StartTime);
                        TimeSpan endTime;
                        if (!string.IsNullOrWhiteSpace(node.EndTime))
                        {
                            endTime = TimeSpanExtensions.FlexibleTimeSpanParse(node.EndTime);
                        }
                        else
                        {
                            var startTimeItem = timings.FirstOrDefault(i => i.StartTime == startTime);
                            if (startTimeItem == null)
                            {
                                var closestTimeItem = timings.OrderBy(i => Math.Abs((i.StartTime - startTime).TotalMilliseconds)).FirstOrDefault();
                                if (closestTimeItem != null && Math.Abs((closestTimeItem.StartTime - startTime).TotalMilliseconds) < 50)
                                {
                                    startTimeItem = closestTimeItem;
                                }
                                else
                                {
                                    throw new Exception($"EndTime not received and StartTime '{startTime:hh\\:mm\\:ss\\.fff}' cannot be matched to an existing item (closest time found:{closestTimeItem?.StartTime ?? TimeSpan.Zero:hh\\:mm\\:ss\\.fff}). {GetSegmentInformation(segment)}");
                                }
                            }
                            endTime = startTimeItem.EndTime;
                        }

                        // Everything left is metadata
                        var extraMetadatas = new MetadataCollection();
                        foreach (var prop in ((JObject)segment).Properties())
                        {
                            if (prop.Value != null
                              && !string.Equals(prop.Name, nameof(TranscriptionNode.StartTime), StringComparison.OrdinalIgnoreCase)
                              && !string.Equals(prop.Name, nameof(TranscriptionNode.EndTime), StringComparison.OrdinalIgnoreCase))
                                extraMetadatas[prop.Name] = prop.Value.ToString();
                        }

                        if (!extraMetadatas.ContainsKey(request.MetadataAlwaysProduced))
                        {
                            throw new Exception($"Required metadata '{request.MetadataAlwaysProduced}' is not present. {GetSegmentInformation(segment)}");
                        }

                        pendingItems.Add((startTime, endTime, extraMetadatas));
                    }

                    // --- SUCCESS COMMIT ---
                    // The entire JSON was flawless. Commit the pending items safely.
                    var itemsAdded = new List<T>();
                    foreach (var pendingItem in pendingItems)
                    {
                        // Add the item only if we don't already have it.
                        if (!r_workingOnContainer.Items.Any(f => f.StartTime == pendingItem.StartTime))
                        {
                            itemsAdded.Add(r_workingOnContainer.AddNewItem(pendingItem.StartTime, pendingItem.EndTime, pendingItem.ExtraMetadatas));
                            increaseNbItemsInCostAction?.Invoke();
                        }
                    }

                    return itemsAdded;
                }
                catch (Exception ex)
                {
                    bool isAIResponseEx = ex is AIResponseException;
                    string fullErrorMsg = ex.Message;

                    if (!isAIResponseEx && currentSegment != null)
                    {
                        fullErrorMsg += $"\n{GetSegmentInformation(currentSegment)}";
                    }

                    if (r_promptUserOnError)
                    {
                        string userFixedText = JsonCorrectionHelper.PromptForFix(fullErrorMsg, fixedJson ?? textToParse);
                        if (userFixedText != null)
                        {
                            textToParse = userFixedText;
                            continue; // Retry loop with the newly typed text
                        }
                    }

                    // If user canceled UI, or UI is disabled, throw normally
                    if (isAIResponseEx) throw;
                    throw new AIResponseException(ex, request, fullErrorMsg, responseReceived, fixedJson);
                }
            }
        }
    }
}