using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public sealed class AIEngineAPI : AIEngine
    {
        public const string ToolName = "GenericAI-API";

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string BaseAddress { get; set; } = "http://localhost:10000";

        [JsonProperty(Order = 11)]
        public string Model { get; set; }

        [JsonProperty(Order = 12)]
        public bool ValidateModelNameInResponse { get; set; } = false;

        [JsonProperty(Order = 13)]
        public string APIKeyName { get; set; }

        [JsonProperty(Order = 14)]
        public TimeSpan TimeOut { get; set; } = TimeSpan.FromSeconds(300);

        [JsonProperty(Order = 15, TypeNameHandling = TypeNameHandling.None)]
        public ExpandoObject RequestBodyExtension { get; set; }

        [JsonProperty(Order = 16)]
        public int? DebugNbRequestsLimit { get; set; } = null;

        [JsonProperty(Order = 17)]
        public bool UseStreaming { get; set; } = true;

        public override void Execute(
            SubtitleGeneratorContext context,
            IEnumerable<AIRequest> requests)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            client.BaseAddress = new Uri(this.BaseAddress.EndsWith("/") ? this.BaseAddress : this.BaseAddress + "/");
            client.Timeout = this.TimeOut;

            if (this.APIKeyName != null)
            {
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + context.GetPrivateConfig(this.APIKeyName));
            }

            var processStartTime = DateTime.Now;
            var lastTimeSaved = DateTime.Now;

            foreach (var request in requests)
            {
                if (DebugNbRequestsLimit != null && request.Number > DebugNbRequestsLimit.Value)
                {
                    throw new AIRequestException(request, $"Stopping because DebugNbRequestsLimit has been reached ({DebugNbRequestsLimit.Value})");
                }

                dynamic requestBody = new ExpandoObject();
                if (this.Model != null)
                {
                    requestBody.model = this.Model;
                }
                requestBody = Merge(requestBody, RequestBodyExtension);
                requestBody.messages = request.Messages;
                if (UseStreaming)
                {
                    requestBody.stream = true;
                }

                var requestBodyAsJson = JsonConvert.SerializeObject(requestBody, Formatting.Indented);

                var verbosePrefix = request.GetVerbosePrefix();
                context.CreateVerboseFile($"{verbosePrefix}-Req.txt", request.FullPrompt, processStartTime);
                context.CreateVerboseFile($"{verbosePrefix}-Req.json", requestBodyAsJson, processStartTime);

                var watch = Stopwatch.StartNew();

                if (UseStreaming)
                {
                    ProcessStreamingResponse(client, request, requestBodyAsJson, context, verbosePrefix, processStartTime, watch);
                }
                else
                {
                    ProcessNormalResponse(client, request, requestBodyAsJson, context, verbosePrefix, processStartTime, watch);
                }

                watch.Stop();

                if (DateTime.Now - lastTimeSaved > TimeSpan.FromMinutes(1))
                {
                    context.CurrentWipsub.Save();
                    lastTimeSaved = DateTime.Now;
                }
            }
        }

        private void ProcessNormalResponse(
            HttpClient client,
            AIRequest request,
            string requestBodyAsJson,
            SubtitleGeneratorContext context,
            string verbosePrefix,
            DateTime processStartTime,
            Stopwatch watch)
        {
            var response = client.PostAsync(
                new Uri(client.BaseAddress, "chat/completions"),
                new StringContent(requestBodyAsJson, Encoding.UTF8, "application/json")
            ).Result;

            if (!response.IsSuccessStatusCode)
            {
                throw new AIRequestException(request, $"Error: {response.StatusCode} - {response.ReasonPhrase}");
            }

            string responseAsJson = response.Content.ReadAsStringAsync().Result;

            dynamic responseBody = JsonConvert.DeserializeObject(responseAsJson);
            context.CreateVerboseFile($"{verbosePrefix}-Resp.json", JsonConvert.SerializeObject(responseBody, Formatting.Indented), processStartTime);

            if (ValidateModelNameInResponse && !string.Equals((string)responseBody.model, this.Model, StringComparison.OrdinalIgnoreCase))
            {
                throw new AIRequestException(request, $"Invalid model name in response:\n" +
                    $"   [API response] {responseBody.model}\n" +
                    $"   [config]   {this.Model}");
            }

            string assistantMessage = responseBody.choices[0].message.content;
            context.CreateVerboseFile($"{verbosePrefix}-Resp.txt", assistantMessage, processStartTime);
            if (assistantMessage == null)
            {
                throw new AIRequestException(request, $"Empty response receive.");
            }

            // Let the request handle the response and store the api cost
            request.HandleResponse(
                context,
                $"{this.BaseAddress},{this.Model}",
                watch.Elapsed,
                assistantMessage,
                (int?)responseBody?.usage?.prompt_tokens,
                (int?)responseBody?.usage?.completion_tokens,
                (int?)responseBody?.usage?.total_tokens);
        }

        private void ProcessStreamingResponse(
            HttpClient client,
            AIRequest request,
            string requestBodyAsJson,
            SubtitleGeneratorContext context,
            string verbosePrefix,
            DateTime processStartTime,
            Stopwatch watch)
        {
            Timer waitingTimer = null;

            try
            {
                var requestId = $"request #{request.Number}";
                context.DefaultProgressUpdateHandler(ToolName, requestId, $"opening connection...");
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(client.BaseAddress, "chat/completions"))
                {
                    Content = new StringContent(requestBodyAsJson, Encoding.UTF8, "application/json")
                };

                // Start a timer that will print waiting messages
                var startTime = DateTime.Now;
                waitingTimer = new Timer(_ =>
                {
                    context.DefaultProgressUpdateHandler(ToolName, requestId, $"waiting for 1st token ({DateTime.Now - startTime})...");
                }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

                context.DefaultProgressUpdateHandler(ToolName, requestId, $"sending request...");
                var response = client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead).Result;

                if (!response.IsSuccessStatusCode)
                {
                    throw new AIRequestException(request, $"Error: {response.StatusCode} - {response.ReasonPhrase}");
                }

                var dataReceived = new StringBuilder();
                var fullContent = new StringBuilder();
                string modelName = null;
                int? promptTokens = null;
                int? completionTokens = null;
                int? totalTokens = null;
                bool doneReceived = false;

                try
                {
                    using (var stream = response.Content.ReadAsStreamAsync().Result)
                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            dataReceived.AppendLine(line);
                            if (string.IsNullOrWhiteSpace(line))
                                continue;

                            if (line.StartsWith("data: "))
                            {
                                var data = line.Substring(6);
                                if (data == "[DONE]")
                                {
                                    doneReceived = true;
                                    break;
                                }

                                try
                                {
                                    dynamic chunk = JsonConvert.DeserializeObject(data);

                                    // Extract model name from first chunk
                                    if (modelName == null && chunk.model != null)
                                    {
                                        modelName = chunk.model;
                                    }

                                    // Extract content delta
                                    if (chunk.choices != null && chunk.choices.Count > 0)
                                    {
                                        var delta = chunk.choices[0].delta;
                                        if (delta != null && delta.content != null)
                                        {
                                            string contentChunk = delta.content;

                                            // Stop the timer on first content token
                                            if (waitingTimer != null)
                                            {
                                                waitingTimer?.Dispose();
                                                waitingTimer = null;
                                            }

                                            fullContent.Append(contentChunk);

                                            // Write to console as chunks arrive
                                            Console.Write(contentChunk);
                                        }
                                    }

                                    // Some APIs send usage information in streaming mode
                                    if (chunk.usage != null)
                                    {
                                        promptTokens = (int?)chunk.usage.prompt_tokens;
                                        completionTokens = (int?)chunk.usage.completion_tokens;
                                        totalTokens = (int?)chunk.usage.total_tokens;
                                    }
                                }
                                catch (JsonException)
                                {
                                    // Skip malformed JSON chunks
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    fullContent.AppendLine();
                    fullContent.AppendLine($"An exception occured while receiving the AI response: {ex.Message}");
                }

                if (!doneReceived)
                {
                    fullContent.Append($"DONE was not received in the response.");
                }
                Console.WriteLine(); // Add newline after streaming output

                string assistantMessage = fullContent.ToString();
                context.CreateVerboseFile($"{verbosePrefix}-Resp.json", dataReceived.ToString(), processStartTime);
                context.CreateVerboseFile($"{verbosePrefix}-Resp.txt", assistantMessage, processStartTime);

                if (ValidateModelNameInResponse && modelName != null && !string.Equals(modelName, this.Model, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AIRequestException(request, $"Invalid model name in response:\n" +
                        $"   [API response] {modelName}\n" +
                        $"   [config]   {this.Model}");
                }

                if (string.IsNullOrEmpty(assistantMessage))
                {
                    throw new AIRequestException(request, $"Empty response received.");
                }

                // Let the request handle the response and store the api cost
                request.HandleResponse(
                    context,
                    $"{this.BaseAddress},{this.Model}",
                    watch.Elapsed,
                    assistantMessage,
                    promptTokens,
                    completionTokens,
                    totalTokens);
            }
            finally
            {
                // Make sure to dispose the timer if it's still running
                waitingTimer?.Dispose();
            }
        }

        private static dynamic Merge(ExpandoObject item1, ExpandoObject item2)
        {
            if (item2 == null)
                return item1;

            var result = new ExpandoObject();
            var d = result as IDictionary<string, object>;

            foreach (var pair in (IDictionary<string, object>)item1)
            {
                d[pair.Key] = pair.Value;
            }
            foreach (var pair in (IDictionary<string, object>)item2)
            {
                d[pair.Key] = pair.Value;
            }

            return result;
        }
    }
}