using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Text;
using FunscriptToolbox.Core.Infra;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Net.Http;
using System.Diagnostics;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIEngineRunner<T> where T: TimedItemWithMetadata
    {
        private readonly SubtitleGeneratorContext r_context;
        private readonly AIEngine r_engine;
        private readonly TimedItemWithMetadataCollection<T> r_workingOnContainer;

        public AIEngineRunner(
            SubtitleGeneratorContext context,
            AIEngine engine,
            TimedItemWithMetadataCollection<T> workingOnContainer)
        {
            r_context = context;
            r_engine = engine;
            r_workingOnContainer = workingOnContainer;
        }

        public void Run(
            AIRequestGenerator workAnalyzer, 
            CachedBinaryGenerator binaryGenerator = null)
        {
            var nbErrors = HandlePreviousFiles(workAnalyzer);
            if (nbErrors == 0)
            {
                try
                {
                    AIRequest request = null;
                    AIRequest lastRequestExecuted = null;
                    int requestNumber = 1;
                    do
                    {
                        request = workAnalyzer.CreateNextRequest(r_context, requestNumber++, lastRequestExecuted, binaryGenerator);
                        if (request != null)
                        {
                            var watch = Stopwatch.StartNew();
                            var response = r_engine.Execute(r_context, request);
                            watch.Stop();

                            var itemsAdded = ParseAssistantMessageAndAddItems(workAnalyzer.GetTimings(), response.AssistantMessage, request);
                            if (response.DraftOfCost != null)
                            {
                                r_workingOnContainer.Costs.Add(new Cost(
                                        response.DraftOfCost.TaskName,
                                        watch.Elapsed,
                                        itemsAdded.Count,
                                        response.DraftOfCost.NbPromptCharacters,
                                        response.DraftOfCost.NbCompletionCharacters,
                                        response.DraftOfCost.NbTotalTokens,
                                        response.DraftOfCost.NbCompletionTokens,
                                        response.DraftOfCost.NbTotalTokens));
                            }

                            if (itemsAdded.Count > 0)
                            {
                                r_context.CurrentWipsub.Save();
                            }

                            lastRequestExecuted = request;
                        }
                    } while (request != null);
                }
                catch (AIRequestException ex)
                {
                    var filepath = ex.Request.GetFilenamePattern(r_context.CurrentBaseFilePath);
                    string body;
                    if (ex.ResponseBodyPartiallyFixed == null)
                    {
                        filepath = filepath.Replace("TODO_", "TODO_E_");
                        body = $"--- Original Prompt ------------------------------\n\n{ex.Request?.FullPrompt}";
                    }
                    else
                    {
                        body = ex.ResponseBodyPartiallyFixed;
                    }
                    r_context.SoftDelete(filepath);
                    File.WriteAllText(filepath, $"{ex.Message.Replace("[", "(").Replace("]", ")")}\n\n{body}", Encoding.UTF8);
                    r_context.AddUserTodo($"Manually fix the following error in file '{Path.GetFileName(filepath)}':\n{ex.Message}");
                    throw;
                }
                catch (Exception ex) when (ex is AggregateException || ex is HttpRequestException)
                {
                    r_context.WriteError($"Error while communicating with the API: {ex.Message}");
                    r_context.WriteLog(ex.ToString());
                    throw new AIRequestException(ex, null, $"Error while communicating with the 'client.BaseAddress' API: {ex.Message}");
                }
            }
        }

        public int HandlePreviousFiles(AIRequestGenerator workAnalyzer)
        {
            var nbErrors = 0;
            var patternSuffix = "_\\d+\\.txt";

            foreach (var fullpath in Directory.GetFiles(
                PathExtension.SafeGetDirectoryName(r_context.CurrentBaseFilePath),
                "*.*"))
            {
                var filename = Path.GetFileName(fullpath);
                if (Regex.IsMatch(
                    filename,
                    $"^" + Regex.Escape($"{Path.GetFileName(r_context.CurrentBaseFilePath)}.TODO_{r_workingOnContainer.Id}") + $"{patternSuffix}$",
                    RegexOptions.IgnoreCase))
                {
                    var response = File.ReadAllText(fullpath);
                    r_context.SoftDelete(fullpath);

                    try
                    {
                        r_context.WriteInfo($"        Analysing existing file '{filename}'...");
                        var nbAdded = ParseAssistantMessageAndAddItems(workAnalyzer.GetTimings(), response);
                        r_context.WriteInfo($"        Finished:");
                        r_context.WriteInfo($"            Nb items added: {nbAdded.Count}");
                        if (nbAdded.Count > 0)
                        {
                            r_context.CurrentWipsub.Save();
                        }
                    }
                    catch (AIRequestException ex)
                    {
                        nbErrors++;
                        File.WriteAllText(fullpath, $"{ex.Message.Replace("[", "(").Replace("]", ")")}\n\n{ex.ResponseBodyPartiallyFixed}", Encoding.UTF8);
                        r_context.WriteInfo($"Error while parsing file '{filename}':{ex.Message}");
                        r_context.AddUserTodo($"Manually fix the following error in file '{filename}':\n{ex.Message}");
                    }
                }
            }

            return nbErrors;
        }

        private List<T> ParseAssistantMessageAndAddItems(
            ITiming[] timings,
            string responseReceived,
            AIRequest request = null)
        {
            try
            {
                dynamic responseArray = ParseAndFixJson(request, responseReceived);

                var itemsAdded = new List<T>();
                foreach (var segment in responseArray)
                {
                    var segmentAsString = JsonConvert.ToString(segment);

                    var seg = (JObject)segment;

                    // Extract and remove known fields
                    var startTime = TimeSpan.Parse((string)seg["StartTime"]);
                    TimeSpan endTime;
                    if (seg.ContainsKey("EndTime"))
                    {
                        endTime = TimeSpan.Parse((string)seg["EndTime"]);
                    }
                    else
                    {
                        var startTimeItem = timings.FirstOrDefault(i => i.StartTime == startTime);
                        if (startTimeItem == null)
                            throw new Exception($"EndTime not received in:\n{segmentAsString}.");
                        endTime = startTimeItem.EndTime;
                    }
                    seg.Remove("StartTime");
                    seg.Remove("EndTime");

                    // Everything left is metadata
                    var extraMetadatas = new MetadataCollection();
                    foreach (var prop in seg.Properties())
                    {
                        if (prop.Value != null)
                            extraMetadatas[prop.Name] = prop.Value.ToString();
                    }

                    if (!extraMetadatas.ContainsKey(request.MetadataAlwaysProduced))
                    {
                        throw new Exception($"Required metadata '{request.MetadataAlwaysProduced}' is not present in: {segmentAsString}");
                    }

                    var tt = r_workingOnContainer.AddNewItem(startTime, endTime, extraMetadatas);
                    itemsAdded.Add(tt);
                }
                return itemsAdded;
            }
            catch (AIRequestException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new AIRequestException(ex, request, ex.Message);
            }
        }

        private static dynamic ParseAndFixJson(AIRequest request, string json, bool aggressiveFix = false)
        {
            try
            {
                // Remove thinking text
                json = Regex.Replace(json, @"\<think\>.*\<\/think\>", string.Empty, RegexOptions.Multiline);
                json = Regex.Replace(json, @"^\s*>.*$", string.Empty, RegexOptions.Multiline);

                var indexOfFirstBracket = json.IndexOf('[');
                if (indexOfFirstBracket >= 0)
                {
                    json = json.Substring(indexOfFirstBracket);
                }

                var indexOfLastBracket = json.LastIndexOf(']');
                if (indexOfLastBracket < 0)
                {
                    // Try to find and remove partial json object
                    var bracesCounter = 0;
                    var lastIndex = 0;
                    for (var index = 0; index < json.Length; index++)
                    {
                        // Make string 'disappear' so that { or } inside the string don't messup bracesCounter
                        if (json[index] == '"')
                        {
                            index++;
                            while (index < json.Length && json[index] != '"')
                            {
                                if (index + 1 < json.Length && json[index] == '\\' && json[index + 1] == '"')
                                {
                                    index += 2;
                                }
                                else
                                {
                                    index++;
                                }
                            }
                            index++; // skip closing "
                        }
                        else
                        {
                            var c = json[index];
                            var diff = (c == '{')
                                ? 1
                                : (c == '}')
                                ? -1
                                : 0;
                            bracesCounter += diff;
                            if (diff != 0 && bracesCounter == 0)
                            {
                                lastIndex = index + 1;
                            }
                        }
                    }

                    json = json.Substring(0, lastIndex);
                    json += "]";
                }
                else
                {
                    json = json.Substring(0, indexOfLastBracket + 1);
                }

                // Add ',' between fields
                json = Regex.Replace(json, @"(""([^""]*)"": ""[^""]*""(?!\s*,))", "$1,");
                // Add ',' between braces
                json = Regex.Replace(json, @"(})(\s*{)", "$1,$2");

                return JsonConvert.DeserializeObject<dynamic>(json);
            }
            catch (Exception ex)
            {
                throw new AIRequestException(ex, request, ex.Message, json);
            }
        }
    }
}