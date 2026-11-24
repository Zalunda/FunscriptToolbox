using FunscriptToolbox.Properties;
using FunscriptToolbox.SubtitlesVerbs.Progress;
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
        private const string PROHIBITED_CONTENT = "PROHIBITED_CONTENT";
        public override string ToolName { get; } = "Google-API";

        [JsonProperty(Order = 30)]
        public bool UseBatchMode { get; set; } = true;

        [JsonProperty(Order = 31)]
        public double UseBatchModeSaving { get; set; } = 0.5;

        [JsonProperty(Order = 32)]
        public bool ValidateImagesOnProbititedResponse { get; set; } = true;

        [JsonProperty(Order = 33)]
        public bool DebugBatchPollingResponse { get; set; }

        protected override double CostSaving => UseBatchMode ? UseBatchModeSaving : 1.0;
        public override string EngineIdentifier => base.EngineIdentifier + (UseBatchMode ? "+Batch" : "+Stream");

        public override AIResponse[] Execute(
            SubtitleGeneratorContext context,
            AIRequest[] requests,
            AIEngineConfigOverride configOverride = null)
        {
            if (requests == null || requests.Length == 0)
            {
                return Array.Empty<AIResponse>();
            }

            if (!this.UseBatchMode && requests.Length > 1)
            {
                throw new NotSupportedException("Multiple requests in IndependentMode are only supported when UseBatchMode is true.");
            }

            using var localResourceLock = AcquireLocalResourceLockIfNeeded(context);

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
                client.BaseAddress = new Uri(this.BaseAddress.EndsWith("/") ? this.BaseAddress : this.BaseAddress + "/");
                client.Timeout = this.TimeOut;

                foreach (var req in requests)
                {
                    var reqPrefix = req.GetVerbosePrefix();
                    context.CreateVerboseTextFile($"{reqPrefix}-Req.txt", $"Base Adress: {this.BaseAddress}\nModel: {this.Model}\n\n{req.FullPrompt}", req.ProcessStartTime);
                    PauseIfEnabled(context, this.PauseBeforeSendingRequest, req.FullPrompt);
                }

                return this.UseBatchMode
                    ? ProcessBatchResponses(context, client, requests, configOverride: configOverride)
                    : RunStoppable(context, client, () => new[] { ProcessStreamingResponse(context, client, requests[0], requests[0].GetVerbosePrefix(), configOverride: configOverride) });
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

        private dynamic CreateRequestBody(AIRequest request, AIEngineConfigOverride configOverride)
        {
            dynamic requestBody = base.CreateRequestBodyWithExtension(configOverride);

            if (request.SystemParts.Any())
            {
                requestBody.systemInstruction = new
                {
                    parts = request.SystemParts.Where(IsSupported).Select(ConvertPart).ToArray()
                };
            }
            if (request.UserParts.Any())
            {
                requestBody.contents = new
                {
                    role = "user",
                    parts = request.UserParts.Where(IsSupported).Select(ConvertPart).ToArray()
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

        private AIResponse[] ProcessBatchResponses(
            SubtitleGeneratorContext context,
            HttpClient client,
            AIRequest[] requests,
            string jsonSuffix = "",
            bool isFirstCall = true,
            AIEngineConfigOverride configOverride = null)
        {
            Timer waitingTimer = null;

            try
            {
                var watch = Stopwatch.StartNew();
                var firstRequest = requests[0];
                var batchJsonPrefix = (requests.Length == 1 ? firstRequest.GetVerbosePrefix() : firstRequest.TaskId) + jsonSuffix;

                dynamic batchRequest = new ExpandoObject();
                batchRequest.batch = new
                {
                    display_name = "my-batch",
                    input_config = new
                    {
                        requests = new
                        {
                            requests = requests.Select((req, idx) => (dynamic)new
                            {
                                request = CreateRequestBody(req, configOverride),
                                metadata = new
                                {
                                    key = $"request-{DateTime.Now:yyyyMMdd-HHmmss}-{idx + 1}"
                                }
                            }).ToArray()
                        }
                    }
                };

                var batchRequestAsJson = JsonConvert.SerializeObject(batchRequest, Formatting.Indented);
                context.CreateVerboseTextFile($"{batchJsonPrefix}-Req.json", batchRequestAsJson, firstRequest.ProcessStartTime);

                context.AIRequestProgressUpdateHandler(ToolName, $"Opening connection...");
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(client.BaseAddress, $"models/{this.Model}:batchGenerateContent?key={context.GetValidatedPrivateConfig(this.APIKeyName)}"))
                {
                    Content = new StringContent(batchRequestAsJson, Encoding.UTF8, "application/json")
                };

                context.AIRequestProgressUpdateHandler(ToolName, $"Sending request...");
                var response = client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead).Result;

                if (!response.IsSuccessStatusCode)
                {
                    throw AIEngineAPIException.FromHttpStatusCode(response, this.EngineIdentifier);
                }

                // Start a timer that will print waiting messages
                GoogleOperation operation = null;
                var startTime = DateTime.Now;
                int? lastErrorCodeReceived = null;
                string lastErrorMessageReceived = null;
                DateTime? firstTimeLastCodeReceived = null;
                var pendingWatch = Stopwatch.StartNew();
                bool cancelRequestedWhileQueuing = false;
                // (the task of this request: its 'Stop now' asks for a confirmation only once the batch is running, when it costs something)
                var requestNode = TaskScope.Current;
                requestNode.SetStopNowWarning(null);

                waitingTimer = new Timer(_ =>
                {
                    var stateText = lastErrorCodeReceived != null
                        ? $" (in ErrorState \"{lastErrorCodeReceived}, {lastErrorMessageReceived}\" for {DateTime.Now - firstTimeLastCodeReceived})"
                        : string.Empty;
                    var duration = DateTime.Now - startTime;

                    if (operation?.GetState() == "BATCH_STATE_RUNNING")
                    {
                        requestNode.SetStopNowWarning(StopNowCostWarning);
                        pendingWatch.Stop();
                        var durationPending = pendingWatch.Elapsed;
                        var durationRunning = duration - durationPending;
                        context.AIRequestProgressUpdateHandler(ToolName, $"Queued: {(int)durationPending.TotalMinutes}:{durationPending:ss}, Running for {(int)durationRunning.TotalMinutes}:{durationRunning:ss}{stateText}... ");
                    }
                    else
                    {
                        context.AIRequestProgressUpdateHandler(ToolName, $"Queued for {(int)duration.TotalMinutes}:{duration:ss}{stateText}... ");
                    }
                }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

                string initialResponseAsJson = response.Content.ReadAsStringAsync().Result;

                operation = JsonConvert.DeserializeObject<GoogleOperation>(initialResponseAsJson);

                if (string.IsNullOrEmpty(operation.Name))
                {
                    throw new AIResponseException(firstRequest, "Batch API returned an empty operation name.");
                }

                if (this.DebugBatchPollingResponse)
                {
                    context.CreateVerboseTextFile($"{batchJsonPrefix}-Batch-Init-Resp.json", initialResponseAsJson, firstRequest.ProcessStartTime);
                }

                string pollJson = null;
                GoogleInlinedResponseLayer2[] inlinedResponses = null;
                int pollNumber = 1;

                // We use the 'name' property (e.g., "batches/zoop...") directly on the BaseAddress
                var operationUrl = $"{operation.Name}?key={context.GetValidatedPrivateConfig(this.APIKeyName)}";
                var cancelOperationUrl = $"{operation.Name}:cancel?key={context.GetValidatedPrivateConfig(this.APIKeyName)}";

                void CancelBatchOnServerAndThrow()
                {
                    waitingTimer?.Dispose();
                    waitingTimer = null;

                    context.AIRequestProgressUpdateHandler(ToolName, "Cancelling batch on Google servers...");

                    try
                    {
                        var cancelUri = new Uri(client.BaseAddress, cancelOperationUrl);

                        var cancelRequest = new HttpRequestMessage(HttpMethod.Post, cancelUri);
                        cancelRequest.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                        var cancelResponse = client.SendAsync(cancelRequest).Result;

                        if (cancelResponse.IsSuccessStatusCode)
                        {
                            context.WriteLog($"Batch {operation?.Name} cancelled successfully.");
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

                while (true)
                {
                    var waitStart = DateTime.Now;
                    var waitDuration = TimeSpan.FromSeconds(10);

                    while (DateTime.Now - waitStart < waitDuration)
                    {
                        // 'Stop now' button of the task ('Pause after this request' is handled by the caller, once the response is saved)
                        if (context.PollStopRequest())
                        {
                            var isCurrentlyQueuing = operation?.GetState() != "BATCH_STATE_RUNNING";
                            if (isCurrentlyQueuing)
                            {
                                // Still queuing: break out of wait to do a final poll (it may have finished meanwhile), then cancel on server.
                                cancelRequestedWhileQueuing = true;
                                waitDuration = TimeSpan.Zero;
                            }
                            else
                            {
                                // Running: the user confirmed that it may still be billed.
                                CancelBatchOnServerAndThrow();
                            }
                        }

                        // Small sleep to prevent high CPU usage while waiting for input
                        Thread.Sleep(100);
                    }

                    var pollResponse = client.GetAsync(operationUrl).Result;
                    if (!pollResponse.IsSuccessStatusCode)
                    {
                        throw AIEngineAPIException.FromHttpStatusCode(pollResponse, this.EngineIdentifier);
                    }

                    pollJson = pollResponse.Content.ReadAsStringAsync().Result;
                    if (this.DebugBatchPollingResponse)
                    {
                        context.CreateVerboseTextFile($"{batchJsonPrefix}-Batch-Polling-{pollNumber++}-Resp.json", pollJson, firstRequest.ProcessStartTime);
                    }

                    operation = JsonConvert.DeserializeObject<GoogleOperation>(pollJson);
                    var state = operation.GetState();
                    var error = operation.GetError();

                    if (error == null)
                    {
                        lastErrorCodeReceived = null;
                        lastErrorMessageReceived = null;
                        firstTimeLastCodeReceived = null;
                    }
                    else
                    {
                        if (lastErrorCodeReceived == null || error.Code != lastErrorCodeReceived)
                        {
                            firstTimeLastCodeReceived = DateTime.Now;
                        }
                        lastErrorCodeReceived = error.Code;
                        lastErrorMessageReceived = error.Message;
                    }

                    // Check cancellation requested while queuing
                    if (cancelRequestedWhileQueuing)
                    {
                        if (error == null && state == "BATCH_STATE_SUCCEEDED")
                        {
                            // (already done: the response is kept, it costs the same)
                            inlinedResponses = operation.GetInlinedResponses();
                            requestNode.ConvertToStopAfter("the batch had already finished, its response is kept.");
                            break;
                        }

                        if (error == null && state == "BATCH_STATE_RUNNING")
                        {
                            // (it started meanwhile: the user didn't confirm a cancel that costs something, so the response is kept)
                            cancelRequestedWhileQueuing = false;
                            requestNode.ConvertToStopAfter("the batch had started running before it could be cancelled, its response will be kept.");
                        }
                        else
                        {
                            CancelBatchOnServerAndThrow();
                        }
                    }

                    if (error != null)
                    {
                        // Ignore errors, keep polling
                    }
                    else if (state == "BATCH_STATE_SUCCEEDED")
                    {
                        inlinedResponses = operation.GetInlinedResponses();
                        break;
                    }
                    else if (!(state == "BATCH_STATE_PENDING" || state == "BATCH_STATE_RUNNING"))
                    {
                        throw new AIResponseException(firstRequest, $"Batch API returned state: {state}. Expecting: BATCH_STATE_SUCCEEDED, BATCH_STATE_PENDING or BATCH_STATE_RUNNING");
                    }
                }

                waitingTimer?.Dispose();
                waitingTimer = null;

                context.CreateVerboseTextFile($"{batchJsonPrefix}-Resp.json", pollJson, firstRequest.ProcessStartTime);

                if (inlinedResponses == null || inlinedResponses.Length == 0)
                {
                    throw new AIResponseException(firstRequest, $"Batch API returned {operation.GetState()} but it didn't include any AI responses.");
                }

                var runningTime = watch.Elapsed - pendingWatch.Elapsed;
                var results = new List<AIResponse>(requests.Length);

                for (int idx = 0; idx < requests.Length; idx++)
                {
                    var req = requests[idx];
                    var expectedKey = $"request-{idx + 1}";
                    var inlinedItem = inlinedResponses.FirstOrDefault(r => r.Metadata?.Key == expectedKey)
                        ?? (idx < inlinedResponses.Length ? inlinedResponses[idx] : null);

                    var finalResult = inlinedItem?.Response;
                    if (finalResult == null)
                    {
                        var errMsg = inlinedItem?.Error != null
                            ? $"Sub-request {expectedKey} failed with error {inlinedItem.Error.Code}: {inlinedItem.Error.Message}"
                            : $"Batch API did not return a response for {expectedKey}.";
                        results.Add(new AIResponse(req, errMsg, Cost.Create(
                            req.TaskId,
                            this.EngineIdentifier,
                            req.SystemParts.Concat(req.UserParts).ToArray(),
                            TimeSpan.FromTicks(watch.Elapsed.Ticks / requests.Length),
                            req.ItemsIncluded?.Length ?? 1,
                            0,
                            this.EstimatedCostPerInputMillionTokens,
                            this.EstimatedCostPerOutputMillionTokens,
                            0, 0, 0, 0, 0)));
                        continue;
                    }

                    var candidates = new StringBuilder();
                    var thoughtContent = new StringBuilder();
                    var extraContent = new StringBuilder();
                    GoogleUsageMetadata googleUsageMetadata = finalResult.UsageMetadata;

                    if (finalResult.PromptFeedback != null)
                    {
                        extraContent.AppendLine();
                        extraContent.AppendLine($"PromptFeedback.BlockReason: {finalResult.PromptFeedback.BlockReason}");
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

                    context.WriteStreamLine();
                    context.WriteStreamLine(thoughtContent.ToString());
                    context.WriteStreamLine(AddRealTime(context, req.StartOffset, candidates.ToString()));

                    extraContent.AppendLine($"PendingTime: {pendingWatch.Elapsed}");
                    extraContent.AppendLine($"RunningTime: {runningTime}");
                    dynamic customInfos = new ExpandoObject();
                    customInfos.PendingTime = $"{pendingWatch.Elapsed:hh\\:mm\\:ss\\.fff}";
                    customInfos.RunningTime = $"{runningTime:hh\\:mm\\:ss\\.fff}";

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

                    context.WriteStreamLine(extraContent.ToString());

                    string assistantMessageExtended = candidates.ToString() + extraContent.ToString();
                    var reqVerbosePrefix = req.GetVerbosePrefix() + jsonSuffix;
                    context.CreateVerboseTextFile($"{reqVerbosePrefix}-Resp.txt", thoughtContent.ToString() + "\n" + new string('*', 80) + "\n" + assistantMessageExtended, req.ProcessStartTime);

                    var unfilteredResponseCost = Cost.Create(
                        req.TaskId,
                        this.EngineIdentifier,
                        req.SystemParts.Concat(req.UserParts).ToArray(),
                        TimeSpan.FromTicks(watch.Elapsed.Ticks / requests.Length),
                        req.ItemsIncluded?.Length ?? 1,
                        0,
                        this.EstimatedCostPerInputMillionTokens * CostSaving,
                        this.EstimatedCostPerOutputMillionTokens * CostSaving,
                        inputTokens: googleUsageMetadata?.PromptTokenCount,
                        outputThoughtsChars: thoughtContent.Length,
                        outputThoughtsTokens: googleUsageMetadata?.ThoughtsTokenCount,
                        outputCandidatesChars: candidates.Length,
                        outputCandidatesTokens: googleUsageMetadata?.CandidatesTokenCount,
                        rawUsageInput: (googleUsageMetadata?.PromptTokensDetails ?? Array.Empty<GooglePromptTokensDetails>())
                            .ToDictionary(item => item.Modality, item => item.TokenCount),
                        customInfos: customInfos);
                    var additionalCostsForImageValidation = new List<Cost>();

                    if (isFirstCall
                        && this.ValidateImagesOnProbititedResponse
                        && finalResult.PromptFeedback?.BlockReason == PROHIBITED_CONTENT
                        && batchRequestAsJson.IndexOf("image/jpeg", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        context.WriteInfo($"Received 'PromptFeedback.BlockReason: {finalResult.PromptFeedback.BlockReason}' on request #{req.Number}, validating image one by one...");
                        var nbImagesRemoved = 0;
                        var filteredRequest = new AIRequest(
                            req.ProcessStartTime,
                            req.Number,
                            req.TaskId,
                            req.ItemsIncluded,
                            req.SystemParts.Select(part => ReplaceProhibitedImage(context, client, req, reqVerbosePrefix, part, additionalCostsForImageValidation, ref nbImagesRemoved)),
                            req.UserParts.Select(part => ReplaceProhibitedImage(context, client, req, reqVerbosePrefix, part, additionalCostsForImageValidation, ref nbImagesRemoved)),
                            req.MetadataAlwaysProduced,
                            req.UpdateMessage,
                            req.StartOffset);
                        if (nbImagesRemoved > 0)
                        {
                            context.WriteInfo($"Processing filtered request #{req.Number}...");
                            var filteredResponse = ProcessBatchResponses(context, client, new[] { filteredRequest }, "-filtered", isFirstCall: false, configOverride)[0];
                            filteredResponse.AdditionalCosts.Add(unfilteredResponseCost);
                            filteredResponse.AdditionalCosts.AddRange(additionalCostsForImageValidation);
                            results.Add(filteredResponse);
                            continue;
                        }
                    }

                    results.Add(new AIResponse(req, assistantMessageExtended, unfilteredResponseCost, additionalCostsForImageValidation));
                }

                PauseIfEnabled(context, this.PauseBeforeSavingResponse);

                return results.ToArray();
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
            string verbosePrefix,
            bool isFirstCall = true,
            AIEngineConfigOverride configOverride = null)
        {
            Timer waitingTimer = null;

            try
            {
                var watch = Stopwatch.StartNew();

                dynamic requestBody = CreateRequestBody(request, configOverride);
                var requestBodyAsJson = JsonConvert.SerializeObject(requestBody, Formatting.Indented);
                context.CreateVerboseTextFile($"{verbosePrefix}-Req.json", requestBodyAsJson, request.ProcessStartTime);

                context.AIRequestProgressUpdateHandler(ToolName, $"Opening connection...");
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(client.BaseAddress, $"models/{this.Model}:streamGenerateContent?key={context.GetValidatedPrivateConfig(this.APIKeyName)}"))
                {
                    Content = new StringContent(requestBodyAsJson, Encoding.UTF8, "application/json")
                };

                var startTime = DateTime.Now;
                waitingTimer = new Timer(_ =>
                {
                    context.AIRequestProgressUpdateHandler(ToolName, $"Waiting for 1st token ({DateTime.Now - startTime})...");
                }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

                context.AIRequestProgressUpdateHandler(ToolName, $"Sending request...");
                var response = client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead).Result;
                AttachToStopWatcher(response);

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
                string promptFeedbackBlockReason = null;
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
                                    context.WriteStreamLine();
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
                                        context.WriteStream(AddRealTime(context, request.StartOffset, currentLineBuffer.ToString()));
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
                                    promptFeedbackBlockReason = googleEvent.PromptFeedback.BlockReason;
                                    extraContent.AppendLine();
                                    extraContent.AppendLine($"PromptFeedback.BlockReason: {googleEvent.PromptFeedback.BlockReason}");
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

                waitingTimer?.Dispose();
                waitingTimer = null;

                context.WriteStream(AddRealTime(context, request.StartOffset, currentLineBuffer.ToString()));
                context.WriteStreamLine();

                extraContent.AppendLine($"RunningTime: {watch.Elapsed}");

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

                context.WriteStreamLine(extraContent.ToString());

                string assistantMessageExtended = candidates.ToString() + extraContent.ToString();
                context.CreateVerboseTextFile($"{verbosePrefix}-Resp.json", chunksReceived.ToString(), request.ProcessStartTime);
                context.CreateVerboseTextFile($"{verbosePrefix}-Resp.txt", thoughtContent.ToString() + "\n" + new string('*', 80) + "\n" + assistantMessageExtended, request.ProcessStartTime);

                var unfilteredResponseCost = Cost.Create(
                    request.TaskId,
                    this.EngineIdentifier,
                    request.SystemParts.Concat(request.UserParts).ToArray(),
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
                        .ToDictionary(item => item.Modality, item => item.TokenCount));
                var additionalCostsForImageValidation = new List<Cost>();

                if (isFirstCall
                    && this.ValidateImagesOnProbititedResponse
                    && promptFeedbackBlockReason == PROHIBITED_CONTENT
                    && requestBodyAsJson.IndexOf("image/jpeg", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    context.WriteInfo($"Received 'PromptFeedback.BlockReason: {promptFeedbackBlockReason}', validating image one by one...");
                    var nbImagesRemoved = 0;
                    var filteredRequest = new AIRequest(
                        request.ProcessStartTime,
                        request.Number,
                        request.TaskId,
                        request.ItemsIncluded,
                        request.SystemParts.Select(part => ReplaceProhibitedImage(context, client, request, verbosePrefix, part, additionalCostsForImageValidation, ref nbImagesRemoved)),
                        request.UserParts.Select(part => ReplaceProhibitedImage(context, client, request, verbosePrefix, part, additionalCostsForImageValidation, ref nbImagesRemoved)),
                        request.MetadataAlwaysProduced,
                        request.UpdateMessage,
                        request.StartOffset);
                    if (nbImagesRemoved > 0)
                    {
                        context.WriteInfo($"Processing filtered request...");
                        var filteredResponse = ProcessStreamingResponse(context, client, filteredRequest, verbosePrefix + "-filtered", isFirstCall: false, configOverride: configOverride);
                        filteredResponse.AdditionalCosts.Add(unfilteredResponseCost);
                        filteredResponse.AdditionalCosts.AddRange(additionalCostsForImageValidation);
                        return filteredResponse;
                    }
                }

                PauseIfEnabled(context, this.PauseBeforeSavingResponse);

                return new AIResponse(request, assistantMessageExtended, unfilteredResponseCost, additionalCostsForImageValidation);
            }
            finally
            {
                waitingTimer?.Dispose();
            }
        }

        private AIRequestPart ReplaceProhibitedImage(SubtitleGeneratorContext context, HttpClient client, AIRequest request, string verbosePrefix, AIRequestPart part, List<Cost> additionalCosts, ref int nbImagesRemoved)
        {
            if (part is AIRequestPartImage partImage)
            {
                var watch = Stopwatch.StartNew();

                var requestBodyAsJson = Resources.ValidateImageRequest.Replace("[IMAGE_BASE64_DATA]", Convert.ToBase64String(partImage.Content));
                context.CreateVerboseTextFile($"{verbosePrefix}-Test-{partImage.FileName}-Req.json", requestBodyAsJson, request.ProcessStartTime);

                var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(client.BaseAddress, $"models/{this.Model}:generateContent?key={context.GetValidatedPrivateConfig(this.APIKeyName)}"))
                {
                    Content = new StringContent(requestBodyAsJson, Encoding.UTF8, "application/json")
                };

                var response = client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead).Result;
                if (!response.IsSuccessStatusCode)
                {
                    throw AIEngineAPIException.FromHttpStatusCode(response, this.EngineIdentifier);
                }

                string responseAsJson = response.Content.ReadAsStringAsync().Result;
                context.CreateVerboseTextFile($"{verbosePrefix}-Test-{partImage.FileName}-Resp.json", responseAsJson, request.ProcessStartTime);

                var serializer = new JsonSerializer();
                var googleEvent = serializer.Deserialize<GoogleResultItem>(new JsonTextReader(new StringReader(responseAsJson)));
                additionalCosts.Add(
                    Cost.Create(
                        request.TaskId,
                        this.EngineIdentifier + "+ValidateImage",
                        new AIRequestPart[] {
                            new AIRequestPartText(part.Section, "X"),
                            part
                        },
                        watch.Elapsed,
                        1,
                        1,
                        this.EstimatedCostPerInputMillionTokens,
                        this.EstimatedCostPerOutputMillionTokens,
                        inputTokens: googleEvent.UsageMetadata?.PromptTokenCount,
                        outputThoughtsChars: 1,
                        outputThoughtsTokens: googleEvent.UsageMetadata?.ThoughtsTokenCount,
                        outputCandidatesChars: 1,
                        outputCandidatesTokens: googleEvent.UsageMetadata?.CandidatesTokenCount,
                        (googleEvent.UsageMetadata?.PromptTokensDetails ?? Array.Empty<GooglePromptTokensDetails>())
                    .ToDictionary(item => item.Modality, item => item.TokenCount)));

                if (responseAsJson.IndexOf(PROHIBITED_CONTENT, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    context.WriteInfo($"Tested image '{partImage.FileName}' [{context.WIP.TimelineMap.ConvertToPartSpecificFileIndexAndTime(partImage.ExtractTime)}] => OK");
                    return part;
                }
                else
                {
                    nbImagesRemoved++;
                    context.WriteInfo($"Tested image '{partImage.FileName}' [{context.WIP.TimelineMap.ConvertToPartSpecificFileIndexAndTime(partImage.ExtractTime)}] => {PROHIBITED_CONTENT} (removed from request)");
                    return new AIRequestPartText(part.Section, "IMAGE_UNAVAILABLE", part.AssociatedDataType);
                }
            }
            else
            {
                return part;
            }
        }

        private class GoogleOperation
        {
            public string Name { get; set; }
            public GoogleBatchMetadata Metadata { get; set; }
            public GoogleBatchResponse Response { get; set; }
            public GoogleError Error { get; set; }

            public string GetState() => Metadata?.State;
            public GoogleError GetError() => this.Error;
            public GoogleInlinedResponseLayer2[] GetInlinedResponses() => this?.Response?.InlinedResponses?.InlinedResponses;
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
            public GoogleRequestMetadata Metadata { get; set; }
        }

        private class GoogleRequestMetadata
        {
            public string Key { get; set; }
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