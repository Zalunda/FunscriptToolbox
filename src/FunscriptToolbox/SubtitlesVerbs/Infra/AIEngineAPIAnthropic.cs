using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIEngineAPIAnthropic : AIEngineAPI
    {
        private const string ANTHROPIC_VERSION = "2023-06-01";
        private const string BATCH_BETA_HEADER = "message-batches-2024-09-24";
        private const string BATCH_STATUS_ENDED = "ended";

        public override string ToolName { get; } = "Anthropic-API";

        [JsonProperty(Order = 30)]
        public bool UseBatchMode { get; set; } = true;

        [JsonProperty(Order = 31)]
        public double UseBatchModeSaving { get; set; } = 0.5;

        [JsonProperty(Order = 32)]
        public bool DebugBatchPollingResponse { get; set; }

        protected override double CostSaving => UseBatchMode ? UseBatchModeSaving : 1.0;
        public override string EngineIdentifier => base.EngineIdentifier + (UseBatchMode ? "+Batch" : "+Stream");

        public override AIResponse Execute(
            SubtitleGeneratorContext context,
            AIRequest request)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("x-api-key", context.GetValidatedPrivateConfig(this.APIKeyName));
                client.DefaultRequestHeaders.Add("anthropic-version", ANTHROPIC_VERSION);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
                client.BaseAddress = new Uri(this.BaseAddress.EndsWith("/") ? this.BaseAddress : this.BaseAddress + "/");
                client.Timeout = this.TimeOut;

                var verbosePrefix = request.GetVerbosePrefix();
                context.CreateVerboseTextFile(
                    $"{verbosePrefix}-Req.txt",
                    $"Base Address: {this.BaseAddress}\nModel: {this.Model}\n\n{request.FullPrompt}",
                    request.ProcessStartTime);

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

        // ─────────────────────────────────────────────────────────────
        //  Request body helpers
        // ─────────────────────────────────────────────────────────────
        private dynamic CreateRequestBody(AIRequest request)
        {
            dynamic body = base.CreateRequestBodyWithExtension();
            body.model = this.Model;

            // System prompt — Anthropic uses a top-level "system" string
            if (request.SystemParts.Any())
            {
                body.system = string.Join("\n",
                    request.SystemParts.OfType<AIRequestPartText>().Select(p => p.Content));
            }

            // User turn
            if (request.UserParts.Any())
            {
                body.messages = new[]
                {
                    new
                    {
                        role    = "user",
                        content = request.UserParts.Select(ConvertPart).ToArray()
                    }
                };
            }
            return body;
        }

        private static dynamic ConvertPart(AIRequestPart part)
        {
            if (part is AIRequestPartImage partImage)
            {
                return new
                {
                    type = "image",
                    source = new
                    {
                        type = "base64",
                        media_type = "image/jpeg",
                        data = Convert.ToBase64String(partImage.Content)
                    }
                };
            }
            else if (part is AIRequestPartAudio)
            {
                // Anthropic does not yet support inline audio in the Messages API.
                // Callers should pre-transcribe audio before reaching this engine.
                throw new NotSupportedException(
                    "Anthropic Messages API does not support inline audio parts.");
            }
            else if (part is AIRequestPartText partText)
            {
                return new { type = "text", text = partText.Content };
            }
            else
            {
                throw new NotSupportedException($"Unsupported part type: {part.GetType().Name}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Batch mode
        //  POST /v1/messages/batches  →  poll  →  GET results_url
        // ─────────────────────────────────────────────────────────────
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

                // Build the batch payload: a single-item batch wrapping the normal request.
                dynamic innerRequest = CreateRequestBody(request);
                var batchPayload = new
                {
                    requests = new[]
                    {
                        new
                        {
                            custom_id = "request-1",
                            @params   = innerRequest   // 'params' is a C# keyword → @params
                        }
                    }
                };

                var batchJson = JsonConvert.SerializeObject(batchPayload, Formatting.Indented);
                context.CreateVerboseTextFile($"{verbosePrefix}-Req.json", batchJson, request.ProcessStartTime);

                // ── 1. Create the batch ──────────────────────────────
                var requestId = $"{request.TaskId}, #{request.Number}: {request.UpdateMessage}";
                context.DefaultProgressUpdateHandler(ToolName, requestId, "Opening connection...");

                var httpRequest = new HttpRequestMessage(
                    HttpMethod.Post, new Uri(client.BaseAddress, "messages/batches"))
                {
                    Content = new StringContent(batchJson, Encoding.UTF8, "application/json")
                };
                httpRequest.Headers.Add("anthropic-beta", BATCH_BETA_HEADER);

                context.DefaultProgressUpdateHandler(ToolName, requestId, "Sending request...");
                var createResponse = client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead).Result;

                if (!createResponse.IsSuccessStatusCode)
                    throw AIEngineAPIException.FromHttpStatusCode(createResponse, this.EngineIdentifier);

                var createJson = createResponse.Content.ReadAsStringAsync().Result;
                var batch = JsonConvert.DeserializeObject<AnthropicBatch>(createJson);

                if (string.IsNullOrEmpty(batch?.id))
                    throw new AIResponseException(request, "Batch API returned an empty batch id.");

                if (this.DebugBatchPollingResponse)
                    context.CreateVerboseTextFile($"{verbosePrefix}-Batch-Init-Resp.json", createJson, request.ProcessStartTime);

                // ── 2. Poll until ended ──────────────────────────────
                var pollUrl = $"messages/batches/{batch.id}";
                var cancelUrl = $"messages/batches/{batch.id}/cancel";
                var startTime = DateTime.Now;
                bool canCancel = false;
                int pollNumber = 1;
                string pollJson = null;

                waitingTimer = new Timer(_ =>
                {
                    canCancel = batch?.processing_status == "in_progress";
                    var cancelMsg = canCancel ? " (PRESS Q TO CANCEL)" : "";
                    var duration = DateTime.Now - startTime;
                    context.DefaultProgressUpdateHandler(ToolName, requestId,
                        $"Queued/Running for {(int)duration.TotalMinutes}:{duration:ss}{cancelMsg}...");
                }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

                while (true)
                {
                    var waitStart = DateTime.Now;
                    var waitDuration = TimeSpan.FromSeconds(10);
                    bool delayedCancel = false;

                    while (DateTime.Now - waitStart < waitDuration)
                    {
                        if (Console.KeyAvailable)
                        {
                            var key = Console.ReadKey(intercept: true);
                            if (canCancel && key.Key == ConsoleKey.Q)
                            {
                                waitDuration = TimeSpan.Zero;
                                delayedCancel = true;
                            }
                        }
                        Thread.Sleep(100);
                    }

                    var pollReq = new HttpRequestMessage(
                        HttpMethod.Get, new Uri(client.BaseAddress, pollUrl));
                    pollReq.Headers.Add("anthropic-beta", BATCH_BETA_HEADER);

                    var pollResp = client.SendAsync(pollReq).Result;
                    if (!pollResp.IsSuccessStatusCode)
                        throw AIEngineAPIException.FromHttpStatusCode(pollResp, this.EngineIdentifier);

                    pollJson = pollResp.Content.ReadAsStringAsync().Result;

                    if (this.DebugBatchPollingResponse)
                        context.CreateVerboseTextFile(
                            $"{verbosePrefix}-Batch-Polling-{pollNumber++}-Resp.json",
                            pollJson, request.ProcessStartTime);

                    batch = JsonConvert.DeserializeObject<AnthropicBatch>(pollJson);
                    var status = batch?.processing_status;

                    if (status == BATCH_STATUS_ENDED)
                        break;

                    if (status != "in_progress")
                        throw new AIResponseException(request,
                            $"Batch API returned unexpected status: {status}");

                    // Handle cancellation request
                    if (delayedCancel)
                    {
                        waitingTimer?.Dispose();
                        waitingTimer = null;

                        context.DefaultProgressUpdateHandler(ToolName, requestId,
                            "Cancelling batch on Anthropic servers...");
                        try
                        {
                            var cancelReq = new HttpRequestMessage(
                                HttpMethod.Post, new Uri(client.BaseAddress, cancelUrl));
                            cancelReq.Headers.Add("anthropic-beta", BATCH_BETA_HEADER);
                            cancelReq.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                            var cancelResp = client.SendAsync(cancelReq).Result;
                            if (cancelResp.IsSuccessStatusCode)
                            {
                                context.WriteLog($"Batch {batch.id} cancelled successfully.");
                                context.WriteInfo("Batch cancelled successfully.");
                            }
                            else
                            {
                                var err = cancelResp.Content.ReadAsStringAsync().Result;
                                context.WriteError(
                                    $"Warning: Failed to cancel batch. Status: {cancelResp.StatusCode}. Details: {err}");
                            }
                        }
                        catch (Exception ex)
                        {
                            context.WriteError($"Error trying to cancel batch: {ex.Message}");
                        }

                        throw new UserStoppedWorkerException();
                    }
                }

                waitingTimer?.Dispose();
                waitingTimer = null;

                context.CreateVerboseTextFile($"{verbosePrefix}-Resp.json", pollJson, request.ProcessStartTime);

                // ── 3. Fetch results (.jsonl from results_url) ───────
                if (string.IsNullOrEmpty(batch?.results_url))
                    throw new AIResponseException(request, "Batch ended but results_url is empty.");

                var resultsReq = new HttpRequestMessage(HttpMethod.Get, batch.results_url);
                resultsReq.Headers.Add("anthropic-beta", BATCH_BETA_HEADER);

                var resultsResp = client.SendAsync(resultsReq).Result;
                if (!resultsResp.IsSuccessStatusCode)
                    throw AIEngineAPIException.FromHttpStatusCode(resultsResp, this.EngineIdentifier);

                var resultsJsonl = resultsResp.Content.ReadAsStringAsync().Result;
                context.CreateVerboseTextFile($"{verbosePrefix}-Resp-Content.json", resultsJsonl, request.ProcessStartTime);

                // ── 4. Parse the JSONL (one line per request) ────────
                AnthropicBatchResultLine resultLine = null;
                foreach (var line in resultsJsonl.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        resultLine = JsonConvert.DeserializeObject<AnthropicBatchResultLine>(trimmed);
                        break; // we only sent one request
                    }
                }

                if (resultLine == null)
                    throw new AIResponseException(request, "Batch results contained no lines.");

                if (resultLine.result?.type == "error")
                    throw new AIResponseException(request,
                        $"Batch result error: {resultLine.result.error?.type}: {resultLine.result.error?.message}");

                var message = resultLine.result?.message;
                if (message == null)
                    throw new AIResponseException(request, "Batch result contained no message.");

                return BuildAIResponse(context, request, watch, verbosePrefix, message);
            }
            finally
            {
                waitingTimer?.Dispose();
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Streaming mode
        //  POST /v1/messages  with stream=true (SSE)
        // ─────────────────────────────────────────────────────────────
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
                requestBody.stream = true;
                var requestBodyAsJson = JsonConvert.SerializeObject(requestBody, Formatting.Indented);
                context.CreateVerboseTextFile($"{verbosePrefix}-Req.json", requestBodyAsJson, request.ProcessStartTime);

                var requestId = $"{request.TaskId}, {request.UpdateMessage}, request #{request.Number}";
                context.DefaultProgressUpdateHandler(ToolName, requestId, "Opening connection...");

                var httpRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri(client.BaseAddress, "messages"))
                {
                    Content = new StringContent(requestBodyAsJson, Encoding.UTF8, "application/json")
                };

                var startTime = DateTime.Now;
                waitingTimer = new Timer(_ =>
                {
                    context.DefaultProgressUpdateHandler(ToolName, requestId, $"Waiting for 1st token ({DateTime.Now - startTime})...");
                }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

                context.DefaultProgressUpdateHandler(ToolName, requestId, "Sending request...");
                var response = client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead).Result;

                if (!response.IsSuccessStatusCode)
                    throw AIEngineAPIException.FromHttpStatusCode(response, this.EngineIdentifier);

                var chunksReceived = new StringBuilder();
                var candidates = new StringBuilder();
                var thoughtContent = new StringBuilder();
                var extraContent = new StringBuilder();
                var currentLineBuffer = new StringBuilder();
                AnthropicUsage usage = null;
                string stopReason = null;

                try
                {
                    using var stream = response.Content.ReadAsStreamAsync().Result;
                    using var reader = new StreamReader(stream);

                    string sseData = null;

                    while (!reader.EndOfStream)
                    {
                        var line = reader.ReadLine();
                        if (line == null) break;

                        chunksReceived.AppendLine(line);

                        if (line.StartsWith("data: "))
                        {
                            sseData = line.Substring(6).Trim();
                        }
                        else if (line.Length == 0 && sseData != null)
                        {
                            // Blank line → dispatch the accumulated event
                            if (waitingTimer != null)
                            {
                                Console.WriteLine();
                                waitingTimer.Dispose();
                                waitingTimer = null;
                            }

                            var evt = JsonConvert.DeserializeObject<AnthropicStreamEvent>(sseData);

                            switch (evt?.type)
                            {
                                case "content_block_delta":
                                    var textDelta = evt.delta?.text;
                                    var thinkingDelta = evt.delta?.thinking;
                                    var textToAppend = textDelta ?? thinkingDelta ?? "";

                                    if (!string.IsNullOrEmpty(textDelta))
                                        candidates.Append(textDelta);
                                    if (!string.IsNullOrEmpty(thinkingDelta))
                                        thoughtContent.Append(thinkingDelta);

                                    if (!string.IsNullOrEmpty(textToAppend))
                                    {
                                        var idxNewLine = textToAppend.LastIndexOf('\n');
                                        if (idxNewLine >= 0)
                                        {
                                            currentLineBuffer.Append(textToAppend.Substring(0, idxNewLine + 1));
                                            Console.Write(AddRealTime(context, request.StartOffset,
                                                currentLineBuffer.ToString()));
                                            currentLineBuffer.Clear();
                                            currentLineBuffer.Append(textToAppend.Substring(idxNewLine + 1));
                                        }
                                        else
                                        {
                                            currentLineBuffer.Append(textToAppend);
                                        }
                                    }
                                    break;

                                case "message_delta":
                                    stopReason = evt.delta?.stop_reason;
                                    if (evt.usage != null) usage = evt.usage;
                                    break;

                                case "message_start":
                                    if (evt.message?.usage != null) usage = evt.message.usage;
                                    break;
                            }

                            sseData = null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    extraContent.AppendLine();
                    extraContent.AppendLine(
                        $"==> An exception occurred while receiving the AI response: {ex.Message}");
                    context.WriteLog(ex.ToString());
                }

                waitingTimer?.Dispose();
                waitingTimer = null;

                Console.Write(AddRealTime(context, request.StartOffset, currentLineBuffer.ToString()));
                Console.WriteLine();

                if (stopReason != null)
                    extraContent.AppendLine($"StopReason: {stopReason}");

                extraContent.AppendLine($"RunningTime: {watch.Elapsed}");

                if (usage != null)
                {
                    extraContent.AppendLine($"InputTokens:  {usage.input_tokens,7}, {GetInputCost(usage.input_tokens):C}");
                    extraContent.AppendLine($"OutputTokens: {usage.output_tokens,7}, {GetOutputCost(usage.output_tokens):C}");
                }

                Console.WriteLine(extraContent.ToString());

                var assistantMessageExtended = candidates.ToString() + extraContent.ToString();

                string verboseOutput = thoughtContent.Length > 0
                    ? thoughtContent.ToString() + "\n" + new string('*', 80) + "\n" + assistantMessageExtended
                    : assistantMessageExtended;

                context.CreateVerboseTextFile($"{verbosePrefix}-Resp.json", chunksReceived.ToString(), request.ProcessStartTime);
                context.CreateVerboseTextFile($"{verbosePrefix}-Resp.txt", verboseOutput, request.ProcessStartTime);

                var cost = Cost.Create(
                    request.TaskId,
                    this.EngineIdentifier,
                    request.SystemParts.Concat(request.UserParts).ToArray(),
                    watch.Elapsed,
                    request.ItemsIncluded?.Length ?? 1,
                    0,
                    this.EstimatedCostPerInputMillionTokens,
                    this.EstimatedCostPerOutputMillionTokens,
                    inputTokens: usage?.input_tokens,
                    outputThoughtsChars: thoughtContent.Length,
                    outputThoughtsTokens: 0, // Anthropic bundles thinking tokens into output_tokens
                    outputCandidatesChars: candidates.Length,
                    outputCandidatesTokens: usage?.output_tokens,
                    rawUsageInput: new Dictionary<string, int>());

                PauseIfEnabled(this.PauseBeforeSavingResponse);

                return new AIResponse(request, assistantMessageExtended, cost, new List<Cost>());
            }
            finally
            {
                waitingTimer?.Dispose();
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Shared: build AIResponse from a completed AnthropicMessage
        // ─────────────────────────────────────────────────────────────
        private AIResponse BuildAIResponse(
            SubtitleGeneratorContext context,
            AIRequest request,
            Stopwatch watch,
            string verbosePrefix,
            AnthropicMessage message)
        {
            var candidates = new StringBuilder();
            var thoughtContent = new StringBuilder();
            var extraContent = new StringBuilder();

            foreach (var block in message.content ?? Array.Empty<AnthropicContentBlock>())
            {
                if (block.type == "text")
                {
                    candidates.Append(block.text ?? string.Empty);
                }
                else if (block.type == "thinking")
                {
                    thoughtContent.Append(block.thinking ?? string.Empty);
                }
            }

            if (message.stop_reason != null)
                extraContent.AppendLine($"StopReason: {message.stop_reason}");

            extraContent.AppendLine($"RunningTime: {watch.Elapsed}");

            var usage = message.usage;
            if (usage != null)
            {
                extraContent.AppendLine($"InputTokens:  {usage.input_tokens,7}, {GetInputCost(usage.input_tokens):C}");
                extraContent.AppendLine($"OutputTokens: {usage.output_tokens,7}, {GetOutputCost(usage.output_tokens):C}");
            }

            Console.WriteLine();
            Console.WriteLine(thoughtContent.ToString());
            Console.WriteLine(AddRealTime(context, request.StartOffset, candidates.ToString()));
            Console.WriteLine(extraContent.ToString());

            var assistantMessageExtended = candidates.ToString() + extraContent.ToString();

            string verboseOutput = thoughtContent.Length > 0
                ? thoughtContent.ToString() + "\n" + new string('*', 80) + "\n" + assistantMessageExtended
                : assistantMessageExtended;

            context.CreateVerboseTextFile(
                $"{verbosePrefix}-Resp.txt", verboseOutput, request.ProcessStartTime);

            var cost = Cost.Create(
                request.TaskId,
                this.EngineIdentifier,
                request.SystemParts.Concat(request.UserParts).ToArray(),
                watch.Elapsed,
                request.ItemsIncluded?.Length ?? 1,
                0,
                this.EstimatedCostPerInputMillionTokens,
                this.EstimatedCostPerOutputMillionTokens,
                inputTokens: usage?.input_tokens,
                outputThoughtsChars: thoughtContent.Length,
                outputThoughtsTokens: 0, // Anthropic bundles thinking tokens into output_tokens
                outputCandidatesChars: candidates.Length,
                outputCandidatesTokens: usage?.output_tokens,
                rawUsageInput: new Dictionary<string, int>());

            PauseIfEnabled(this.PauseBeforeSavingResponse);

            return new AIResponse(request, assistantMessageExtended, cost, new List<Cost>());
        }

        // ─────────────────────────────────────────────────────────────
        //  Anthropic-specific DTOs
        // ─────────────────────────────────────────────────────────────

        private class AnthropicBatch
        {
            public string id { get; set; }
            public string processing_status { get; set; }
            public string results_url { get; set; }
            public AnthropicRequestCounts request_counts { get; set; }
        }

        private class AnthropicRequestCounts
        {
            public int processing { get; set; }
            public int succeeded { get; set; }
            public int errored { get; set; }
            public int canceled { get; set; }
            public int expired { get; set; }
        }

        // One line in the .jsonl results file
        private class AnthropicBatchResultLine
        {
            public string custom_id { get; set; }
            public AnthropicBatchResult result { get; set; }
        }

        private class AnthropicBatchResult
        {
            public string type { get; set; }  // "message" | "error"
            public AnthropicMessage message { get; set; }
            public AnthropicBatchError error { get; set; }
        }

        private class AnthropicBatchError
        {
            public string type { get; set; }
            public string message { get; set; }
        }

        private class AnthropicMessage
        {
            public AnthropicContentBlock[] content { get; set; }
            public string stop_reason { get; set; }
            public AnthropicUsage usage { get; set; }
        }

        private class AnthropicContentBlock
        {
            public string type { get; set; }
            public string text { get; set; }
            public string thinking { get; set; }
        }

        private class AnthropicUsage
        {
            public int input_tokens { get; set; }
            public int output_tokens { get; set; }
        }

        // SSE streaming events
        private class AnthropicStreamEvent
        {
            public string type { get; set; }
            public AnthropicDelta delta { get; set; }
            public AnthropicUsage usage { get; set; }
            public AnthropicMessage message { get; set; }
        }

        private class AnthropicDelta
        {
            public string type { get; set; }
            public string text { get; set; }
            public string thinking { get; set; }
            public string stop_reason { get; set; }
        }
    }
}