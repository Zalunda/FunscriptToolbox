using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIEngineAPIGoogle : AIEngineAPI
    {
        public override string ToolName { get; } = "Google-API";

        [JsonProperty(Order = 30)]
        public bool UseBatchMode { get; set; } = true;

        [JsonProperty(Order = 31)]
        public bool DebugBatchPollingResponse { get; set; }

        public override AIResponse Execute(
            SubtitleGeneratorContext context,
            AIRequest request)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
                client.BaseAddress = new Uri(this.BaseAddress.EndsWith("/") ? this.BaseAddress : this.BaseAddress + "/");
                client.Timeout = this.TimeOut;

                var verbosePrefix = request.GetVerbosePrefix();
                context.CreateVerboseTextFile($"{verbosePrefix}-Req.txt", $"Base Adress: {this.BaseAddress}\nModel: {this.Model}\n\n{request.FullPrompt}", request.ProcessStartTime);

                PauseIfEnabled(this.PauseBeforeSendingRequest, request.FullPrompt);

                return this.UseBatchMode
                    ? ProcessBatchResponse(context, client, request, verbosePrefix)
                    : ProcessStreamingResponse(context, client, request, verbosePrefix);
            }
            catch (AggregateException ex)
            {
                context.WriteLog(ex.ToString());
                throw AIEngineAPIException.FromAggregateException(ex, this.EngineIdentifier);
            }
            catch (HttpRequestException ex)
            {
                context.WriteLog(ex.ToString());
                throw AIEngineAPIException.FromHttpRequestException(ex, this.EngineIdentifier);
            }
        }

        private dynamic CreateRequestBody(AIRequest request)
        {
            dynamic requestBody = base.CreateRequestBodyWithExtension();

            if (request.SystemParts.Any())
            {
                requestBody.systemInstruction = new
                {
                    parts = request.SystemParts.Select(ConvertPart).ToArray()
                };
            }
            if (request.UserParts.Any())
            {
                requestBody.contents = new
                {
                    role = "user",
                    parts = request.UserParts.Select(ConvertPart).ToArray()
                };
            }
            return requestBody;
        }

        private static dynamic ConvertPart(AIRequestPart part)
        {
            if (part is AIRequestPartAudio partAudio)
            {
                return new
                {
                    inlineData = new
                    {
                        mimeType = "audio/wav",
                        data = Convert.ToBase64String(partAudio.Content)
                    }
                };
            }
            else if (part is AIRequestPartImage partImage)
            {
                return new
                {
                    inlineData = new
                    {
                        mimeType = "image/jpeg",
                        data = Convert.ToBase64String(partImage.Content)
                    }
                };
            }
            else if (part is AIRequestPartText partText)
            {
                return new
                {
                    text = partText.Content
                };
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        private AIResponse ProcessBatchResponse(
            SubtitleGeneratorContext context,
            HttpClient client,
            AIRequest request,
            string verbosePrefix)
        {
            Timer waitingTimer = null;

            try
            {
                var watch = Stopwatch.StartNew();

                dynamic innerRequestBody = CreateRequestBody(request);

                dynamic batchRequest = new ExpandoObject();
                batchRequest.batch = new
                {
                    display_name = "my-batch",
                    input_config = new
                    {
                        requests = new
                        {
                            requests = new dynamic[]
                            {
                                new
                                {
                                    request = innerRequestBody,
                                    metadata = new {
                                        key = "request-1"
                                    }
                                }
                            }
                        }
                    } 
                };

                var batchRequestAsJson = JsonConvert.SerializeObject(batchRequest, Formatting.Indented);
                context.CreateVerboseTextFile($"{verbosePrefix}-Req.json", batchRequestAsJson, request.ProcessStartTime);

                var requestId = $"{request.TaskId}, #{request.Number}: {request.UpdateMessage}";
                context.DefaultProgressUpdateHandler(ToolName, requestId, $"Opening connection...");
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(client.BaseAddress, $"models/{this.Model}:batchGenerateContent?key={context.GetValidatedPrivateConfig(this.APIKeyName)}"))
                {
                    Content = new StringContent(batchRequestAsJson, Encoding.UTF8, "application/json")
                };

                context.DefaultProgressUpdateHandler(ToolName, requestId, $"Sending request...");
                var response = client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead).Result;

                if (!response.IsSuccessStatusCode)
                {
                    throw AIEngineAPIException.FromHttpStatusCode(response, this.EngineIdentifier);
                }

                // Start a timer that will print waiting messages
                GoogleOperation operation = null;
                bool canCancel = false;
                var startTime = DateTime.Now;
                waitingTimer = new Timer(_ =>
                {
                    canCancel = operation?.GetState() == "BATCH_STATE_PENDING";
                    var cancelMessage = canCancel ? " => PRESS Q TO CANCEL" : "";
                    context.DefaultProgressUpdateHandler(ToolName, requestId, $"Waited {DateTime.Now - startTime:mm\\:ss} for batch response (State: {operation?.GetState()?.Replace("BATCH_STATE_", "")}{cancelMessage})... ");
                }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

                string initialResponseAsJson = response.Content.ReadAsStringAsync().Result;

                // 1. Parse the Initial Operation Response
                operation = JsonConvert.DeserializeObject<GoogleOperation>(initialResponseAsJson);

                if (string.IsNullOrEmpty(operation.Name))
                {
                    throw new AIResponseException(request, "Batch API returned an empty operation name.");
                }

                if (this.DebugBatchPollingResponse)
                {
                    context.CreateVerboseTextFile($"{verbosePrefix}-Batch-Init-Resp.json", initialResponseAsJson, request.ProcessStartTime);
                }

                // 2. Poll for Completion
                string pollJson = null;
                GoogleResultItem finalResult = null;
                int pollNumber = 1;

                // We use the 'name' property (e.g., "batches/zoop...") directly on the BaseAddress
                var operationUrl = $"{operation.Name}?key={context.GetValidatedPrivateConfig(this.APIKeyName)}";
                var cancelOperationUrl = $"{operation.Name}:cancel?key={context.GetValidatedPrivateConfig(this.APIKeyName)}";

                while (true)
                {
                    var waitStart = DateTime.Now;
                    var waitDuration = TimeSpan.FromSeconds(10);
                    var delayedCancel = false;

                    while (DateTime.Now - waitStart < waitDuration)
                    {
                        if (Console.KeyAvailable)
                        {
                            var keyInfo = Console.ReadKey(intercept: true);
                            if (canCancel && keyInfo.Key == ConsoleKey.Q)
                            {
                                // We do one last update before cancelling.
                                waitDuration = TimeSpan.Zero;
                                delayedCancel = true;
                            }
                        }

                        // Small sleep to prevent high CPU usage while waiting for input
                        Thread.Sleep(100);
                    }

                    var pollResponse = client.GetAsync(operationUrl).Result;
                    if (!response.IsSuccessStatusCode)
                    {
                        throw AIEngineAPIException.FromHttpStatusCode(response, this.EngineIdentifier);
                    }

                    pollJson = pollResponse.Content.ReadAsStringAsync().Result;
                    if (this.DebugBatchPollingResponse)
                    {
                        context.CreateVerboseTextFile($"{verbosePrefix}-Batch-Polling-{pollNumber++}-Resp.json", pollJson, request.ProcessStartTime);
                    }

                    operation = JsonConvert.DeserializeObject<GoogleOperation>(pollJson);
                    var state = operation.GetState();
                    var error = operation.GetError();
                    if (state == "BATCH_STATE_SUCCEEDED")
                    {
                        finalResult = operation.GetResultItem();
                        break;
                    }
                    else if (error != null)
                    {
                        throw new AIResponseException(request, $"Batch API returned:\nState: {state}\nCode:{error.Code}\nMessage: {error.Message}");
                    }
                    else if (!(state == "BATCH_STATE_PENDING" || state == "BATCH_STATE_RUNNING"))
                    {
                        throw new AIResponseException(request, $"Batch API returned state: {state}. Expecting: BATCH_STATE_SUCCEEDED, BATCH_STATE_PENDING or BATCH_STATE_RUNNING");
                    }
                    else if (state == "BATCH_STATE_PENDING" && delayedCancel)
                    {
                        waitingTimer?.Dispose();
                        waitingTimer = null;

                        context.DefaultProgressUpdateHandler(ToolName, requestId, "Cancelling batch on Google servers...");

                        try
                        {
                            var cancelUri = new Uri(client.BaseAddress, cancelOperationUrl);

                            var cancelRequest = new HttpRequestMessage(HttpMethod.Post, cancelUri);
                            cancelRequest.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                            var cancelResponse = client.SendAsync(cancelRequest).Result;

                            if (cancelResponse.IsSuccessStatusCode)
                            {
                                context.WriteLog($"Batch {operation.Name} cancelled successfully.");
                                context.WriteInfo($"Batch cancelled successfully.");
                            }
                            else
                            {
                                var errorContent = cancelResponse.Content.ReadAsStringAsync().Result;
                                context.WriteError($"Warning: Failed to cancel batch. Status: {cancelResponse.StatusCode}. Details: {errorContent}");
                            }
                        }
                        catch (Exception ex)
                        {
                            context.WriteError($"Error trying to cancel batch: {ex.Message}");
                        }

                        throw new UserStoppedWorkerException();
                    }

                    // we loop...
                }

                context.CreateVerboseTextFile($"{verbosePrefix}-Resp.json", pollJson, request.ProcessStartTime);

                // 3. Process the Result

                if (finalResult == null)
                {
                    throw new AIResponseException(request, $"Batch API returned {operation.GetState()} but it didn't include the AI response.");
                }

                // Reuse your existing builders
                var candidates = new StringBuilder();
                var thoughtContent = new StringBuilder();
                var extraContent = new StringBuilder();
                GoogleUsageMetadata googleUsageMetadata = finalResult.UsageMetadata;

                // Map the Batch Candidates to your logic
                if (finalResult.PromptFeedback != null)
                {
                    extraContent.AppendLine();
                    extraContent.AppendLine($"BlockReason: {finalResult.PromptFeedback.BlockReason}");
                }
                foreach (var candidate in finalResult.Candidates ?? Array.Empty<GoogleCandidate>())
                {
                    foreach (var part in candidate.Content?.Parts ?? Array.Empty<GooglePart>())
                    {
                        if (part.Thought == true)
                        {
                            thoughtContent.Append(part.Text);
                        }
                        else
                        {
                            candidates.Append(part.Text);
                        }
                    }

                    if (candidate.FinishReason != null)
                    {
                        extraContent.AppendLine();
                        extraContent.AppendLine($"FinishReason: {candidate.FinishReason}");
                    }
                }

                // Print to console to mimic streaming feedback (all at once now)
                Console.WriteLine();
                Console.WriteLine(AddRealTime(context, request.StartOffset, candidates.ToString()));

                // 4. Calculate Usage and Costs
                if (googleUsageMetadata != null)
                {
                    extraContent.AppendLine($"InputTokensByModality:     {googleUsageMetadata.PromptTokenCount,7}, {GetInputCost(googleUsageMetadata.PromptTokenCount):C}");
                    foreach (var item in googleUsageMetadata.PromptTokensDetails ?? Array.Empty<GooglePromptTokensDetails>())
                    {
                        extraContent.AppendLine($"   {item.Modality,10}: {item.TokenCount,7}");
                    }
                    var totalOutputTokens = googleUsageMetadata.ThoughtsTokenCount + googleUsageMetadata.CandidatesTokenCount;
                    extraContent.AppendLine($"OutputTextTokensBySection: {totalOutputTokens,7}, {GetOutputCost(totalOutputTokens):C}");
                    extraContent.AppendLine($"     Thoughts: {googleUsageMetadata.ThoughtsTokenCount,7}");
                    extraContent.AppendLine($"   Candidates: {googleUsageMetadata.CandidatesTokenCount,7}");
                }

                Console.WriteLine(extraContent.ToString());

                string assistantMessageExtended = candidates.ToString() + extraContent.ToString();
                context.CreateVerboseTextFile($"{verbosePrefix}-Resp.txt", thoughtContent.ToString() + "\n" + new string('*', 80) + "\n" + assistantMessageExtended, request.ProcessStartTime);

                PauseIfEnabled(this.PauseBeforeSavingResponse);

                return new AIResponse(request, assistantMessageExtended, Cost.Create(
                    request.TaskId,
                    this.EngineIdentifier,
                    request,
                    watch.Elapsed,
                    request.ItemsIncluded?.Length ?? 1,
                    0,
                    this.EstimatedCostPerInputMillionTokens,
                    this.EstimatedCostPerOutputMillionTokens,
                    inputTokens: googleUsageMetadata?.PromptTokenCount,
                    outputThoughtsChars: thoughtContent.Length,
                    outputThoughtsTokens: googleUsageMetadata?.ThoughtsTokenCount,
                    outputCandidatesChars: candidates.Length,
                    outputCandidatesTokens: googleUsageMetadata?.CandidatesTokenCount,
                    (googleUsageMetadata?.PromptTokensDetails ?? Array.Empty<GooglePromptTokensDetails>())
                        .ToDictionary(item => item.Modality, item => item.TokenCount)));
            }
            finally
            {
                waitingTimer?.Dispose();
            }
        }


        private AIResponse ProcessStreamingResponse(
            SubtitleGeneratorContext context,
            HttpClient client,
            AIRequest request,
            string verbosePrefix)
        {
            Timer waitingTimer = null;

            try
            {
                var watch = Stopwatch.StartNew();

                dynamic requestBody = CreateRequestBody(request);
                var requestBodyAsJson = JsonConvert.SerializeObject(requestBody, Formatting.Indented);
                context.CreateVerboseTextFile($"{verbosePrefix}-Req.json", requestBodyAsJson, request.ProcessStartTime);

                var requestId = $"{request.TaskId}, {request.UpdateMessage}, request #{request.Number}";
                context.DefaultProgressUpdateHandler(ToolName, requestId, $"Opening connection...");
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(client.BaseAddress, $"models/{this.Model}:streamGenerateContent?key={context.GetValidatedPrivateConfig(this.APIKeyName)}"))
                {
                    Content = new StringContent(requestBodyAsJson, Encoding.UTF8, "application/json")
                };

                // Start a timer that will print waiting messages
                var startTime = DateTime.Now;
                waitingTimer = new Timer(_ =>
                {
                    context.DefaultProgressUpdateHandler(ToolName, requestId, $"Waiting for 1st token ({DateTime.Now - startTime})...");
                }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

                context.DefaultProgressUpdateHandler(ToolName, requestId, $"Sending request...");
                var response = client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead).Result;

                if (!response.IsSuccessStatusCode)
                {
                    throw AIEngineAPIException.FromHttpStatusCode(response, this.EngineIdentifier);
                }

                var serializer = new JsonSerializer();

                var chunksReceived = new StringBuilder();
                var candidates = new StringBuilder();
                var thoughtContent = new StringBuilder();
                var extraContent = new StringBuilder();
                var currentLineBuffer = new StringBuilder();
                GoogleUsageMetadata googleUsageMetadata = null;

                try
                {
                    using (var stream = response.Content.ReadAsStreamAsync().Result)
                    using (var reader = new StreamReader(stream))
                    using (var jsonReader = new JsonTextReader(reader))
                    {
                        while (jsonReader.Read())
                        {
                            if (jsonReader.TokenType == JsonToken.StartObject)
                            {
                                if (waitingTimer != null)
                                {
                                    Console.WriteLine();
                                    waitingTimer?.Dispose();
                                    waitingTimer = null;
                                }

                                var chunk = serializer.Deserialize<dynamic>(jsonReader);
                                var chunkAsString = chunk.ToString();
                                chunksReceived.AppendLine(chunkAsString);

                                var googleEvent = serializer.Deserialize<GoogleResultItem>(new JsonTextReader(new StringReader(chunkAsString)));

                                foreach (var part in googleEvent.GetParts())
                                {
                                    if (part.Thought == true)
                                    {
                                        thoughtContent.Append(part.Text);
                                    }
                                    else
                                    {
                                        candidates.Append(part.Text);
                                    }

                                    var indexOfLastNewLine = part.Text.LastIndexOf('\n');
                                    if (indexOfLastNewLine >= 0)
                                    {
                                        currentLineBuffer.Append(part.Text.Substring(0, indexOfLastNewLine + 1));
                                        Console.Write(AddRealTime(context, request.StartOffset, currentLineBuffer.ToString()));
                                        currentLineBuffer.Clear();
                                        currentLineBuffer.Append(part.Text.Substring(indexOfLastNewLine + 1));
                                    }
                                    else
                                    {
                                        currentLineBuffer.Append(part.Text);
                                    }
                                }

                                var candidate = googleEvent.GetFinishReason();
                                if (candidate != null)
                                {
                                    extraContent.AppendLine();
                                    extraContent.AppendLine($"FinishReason: {candidate.FinishReason}");
                                }

                                if (googleEvent.PromptFeedback != null)
                                {
                                    extraContent.AppendLine();
                                    extraContent.AppendLine($"BlockReason: {googleEvent.PromptFeedback.BlockReason}");
                                }

                                googleUsageMetadata = googleEvent.UsageMetadata ?? googleUsageMetadata;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    extraContent.AppendLine();
                    extraContent.AppendLine();
                    extraContent.AppendLine($"==> An exception occured while receiving the AI response: {ex.Message}");
                    context.WriteLog(ex.ToString());
                }

                Console.Write(AddRealTime(context, request.StartOffset, currentLineBuffer.ToString()));
                Console.WriteLine();

                if (googleUsageMetadata != null)
                {
                    extraContent.AppendLine($"InputTokensByModality:     {googleUsageMetadata.PromptTokenCount,7}, {GetInputCost(googleUsageMetadata.PromptTokenCount):C}");
                    foreach (var item in googleUsageMetadata.PromptTokensDetails ?? Array.Empty<GooglePromptTokensDetails>())
                    {
                        extraContent.AppendLine($"   {item.Modality,10}: {item.TokenCount,7}");
                    }
                    var totalOutputTokens = googleUsageMetadata.ThoughtsTokenCount + googleUsageMetadata.CandidatesTokenCount;
                    extraContent.AppendLine($"OutputTextTokensBySection: {totalOutputTokens,7}, {GetOutputCost(totalOutputTokens):C}");
                    extraContent.AppendLine($"     Thoughts: {googleUsageMetadata.ThoughtsTokenCount,7}");
                    extraContent.AppendLine($"   Candidates: {googleUsageMetadata.CandidatesTokenCount,7}");
                }

                Console.WriteLine(extraContent.ToString());

                string assistantMessageExtended = candidates.ToString() + extraContent.ToString();
                context.CreateVerboseTextFile($"{verbosePrefix}-Resp.json", chunksReceived.ToString(), request.ProcessStartTime);
                context.CreateVerboseTextFile($"{verbosePrefix}-Resp.txt", thoughtContent.ToString() + "\n" + new string('*', 80) + "\n" + assistantMessageExtended, request.ProcessStartTime);

                PauseIfEnabled(this.PauseBeforeSavingResponse);

                return new AIResponse(request, assistantMessageExtended, Cost.Create(
                    request.TaskId,
                    this.EngineIdentifier,
                    request,
                    watch.Elapsed,
                    request.ItemsIncluded?.Length ?? 1,
                    0,
                    this.EstimatedCostPerInputMillionTokens,
                    this.EstimatedCostPerOutputMillionTokens,
                    inputTokens: googleUsageMetadata?.PromptTokenCount,
                    outputThoughtsChars: thoughtContent.Length,
                    outputThoughtsTokens: googleUsageMetadata?.ThoughtsTokenCount,
                    outputCandidatesChars: candidates.Length,
                    outputCandidatesTokens: googleUsageMetadata?.CandidatesTokenCount,
                    (googleUsageMetadata?.PromptTokensDetails ?? Array.Empty<GooglePromptTokensDetails>())
                        .ToDictionary(item => item.Modality, item => item.TokenCount)));
            }
            finally
            {
                waitingTimer?.Dispose();
            }
        }

        private class GoogleOperation
        {
            public string Name { get; set; }
            public GoogleBatchMetadata Metadata { get; set; }
            public GoogleBatchResponse Response { get; set; }
            public GoogleError Error { get; set; }

            public string GetState() => Metadata?.State;
            public GoogleError GetError() => this.Error ?? this?.Response?.InlinedResponses?.InlinedResponses?.FirstOrDefault()?.Error;
            public GoogleResultItem GetResultItem() => this?.Response?.InlinedResponses?.InlinedResponses?.FirstOrDefault()?.Response;
        }

        private class GoogleBatchMetadata
        {
            public string State { get; set; }
        }

        private class GoogleBatchResponse
        {
            public GoogleInlinedResponseLayer1 InlinedResponses { get; set; }
        }

        private class GoogleInlinedResponseLayer1
        {
            public GoogleInlinedResponseLayer2[] InlinedResponses { get; set; }
        }

        private class GoogleInlinedResponseLayer2
        {
            public GoogleResultItem Response { get; set; }
            public GoogleError Error { get; set; }
        }

        private class GoogleError
        {
            public int Code { get; set; }
            public string Message { get; set; }
        }

        private class GoogleResultItem
        {
            public GoogleCandidate[] Candidates { get; set; }
            public GooglePromptFeedback PromptFeedback { get; set; }
            public GoogleUsageMetadata UsageMetadata { get; set; }

            public IEnumerable<GooglePart> GetParts()
            {
                foreach (var candidate in this.Candidates ?? Array.Empty<GoogleCandidate>())
                {
                    foreach (var part in candidate.Content?.Parts ?? Array.Empty<GooglePart>())
                    {
                        if (part.Text != null)
                        {
                            yield return part;
                        }
                    }
                }
            }

            public GoogleCandidate GetFinishReason()
            {
                return this.Candidates?.LastOrDefault(candidate => candidate.FinishReason != null);
            }
        }

        private class GoogleUsageMetadata
        {
            public int PromptTokenCount { get; set; }
            public GooglePromptTokensDetails[] PromptTokensDetails { get; set; }

            public int CandidatesTokenCount { get; set; }
            public int ThoughtsTokenCount { get; set; }

            // TODO something with this??
            public int CachedContentTokenCount { get; set; } 
            public GooglePromptTokensDetails[] CacheTokensDetails { get; set; }
        }

        public class GooglePromptFeedback
        {
            public string BlockReason { get; set; }
        }

        public class GooglePromptTokensDetails 
        { 
            public string Modality { get; set; }
            public int TokenCount { get; set; }
        }

        private class GoogleCandidate
        {
            public GoogleContent Content { get; set; }
            public string FinishReason { get; set; }
            public string FinishMessage { get; set; }
        }

        private class GoogleContent
        {
            public GooglePart[] Parts { get; set; }
            public string Role { get; set; }
            public int index { get; set; }
        }

        private class GooglePart
        {
            public string Text { get; set; }
            public bool? Thought { get; set; }
        }
    }
}