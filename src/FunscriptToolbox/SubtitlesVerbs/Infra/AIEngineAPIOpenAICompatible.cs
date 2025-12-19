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
    public class AIEngineAPIOpenAICompatible : AIEngineAPI
    {
        public override string ToolName { get; } = "OpenAI-API";

        [JsonProperty(Order = 30)]
        public bool UseStreaming { get; set; } = true;

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

                if (this.APIKeyName != null)
                {
                    client.DefaultRequestHeaders.Add("Authorization", "Bearer " + context.GetValidatedPrivateConfig(this.APIKeyName));
                }

                dynamic requestBody = CreateRequestBody(request);
                var requestBodyAsJson = JsonConvert.SerializeObject(requestBody, Formatting.Indented);

                var verbosePrefix = request.GetVerbosePrefix();
                context.CreateVerboseTextFile($"{verbosePrefix}-Req.txt", $"Base Adress: {this.BaseAddress}\nModel: {this.Model}\n\n{request.FullPrompt}", request.ProcessStartTime);
                context.CreateVerboseTextFile($"{verbosePrefix}-Req.json", requestBodyAsJson, request.ProcessStartTime);

                PauseIfEnabled(this.PauseBeforeSendingRequest, request.FullPrompt);

                var response = UseStreaming == true
                    ? ProcessStreamingResponse(client, request, requestBodyAsJson, context, verbosePrefix)
                    : ProcessNormalResponse(client, request, requestBodyAsJson, context, verbosePrefix);

                return response;
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
            if (this.Model != null)
            {
                requestBody.model = this.Model;
            }
            if (UseStreaming)
            {
                requestBody.stream = true;
            }

            var messages = new List<dynamic>();
            if (request.SystemParts.Any())
            {
                messages.Add(
                    new
                    {
                        role = "system",
                        content = request.SystemParts.Where(IsSupported).Select(ConvertPart).ToArray()
                    });
            }
            if (request.UserParts.Any())
            {
                messages.Add(
                    new
                    {
                        role = "user",
                        content = request.UserParts.Where(IsSupported).Select(ConvertPart).ToArray()
                    });
            }
            requestBody.messages = messages;
            return requestBody;
        }

        protected virtual dynamic ConvertPart(AIRequestPart part)
        {
            if (part is AIRequestPartAudio partAudio)
            {
                return new
                {
                    type = "input_audio",
                    input_audio = new
                    {
                        data = Convert.ToBase64String(partAudio.Content),
                        format = "wav"
                    }
                };
            }
            else if (part is AIRequestPartImage partImage)
            {
                return new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = $"data:image/jpeg;base64,{Convert.ToBase64String(partImage.Content)}"
                    }
                };
            }
            else if (part is AIRequestPartText partText)
            {
                return new
                {
                    type = "text",
                    text = partText.Content
                };
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        private AIResponse ProcessNormalResponse(
            HttpClient client,
            AIRequest request,
            string requestBodyAsJson,
            SubtitleGeneratorContext context,
            string verbosePrefix)
        {
            var watch = Stopwatch.StartNew();

            var requestId = $"{request.TaskId}, {request.UpdateMessage}, request #{request.Number}";
            context.DefaultProgressUpdateHandler(ToolName, requestId, $"Sending request...");
            var response = client.PostAsync(
                new Uri(client.BaseAddress, "chat/completions"),
                new StringContent(requestBodyAsJson, Encoding.UTF8, "application/json")
            ).Result;

            if (!response.IsSuccessStatusCode)
            {
                throw AIEngineAPIException.FromHttpStatusCode(response, this.EngineIdentifier);
            }

            string responseAsJson = response.Content.ReadAsStringAsync().Result;

            dynamic responseBody = JsonConvert.DeserializeObject(responseAsJson);
            context.CreateVerboseTextFile($"{verbosePrefix}-Resp.json", JsonConvert.SerializeObject(responseBody, Formatting.Indented), request.ProcessStartTime);

            if (ValidateModelNameInResponse && !string.Equals((string)responseBody.model, this.Model, StringComparison.OrdinalIgnoreCase))
            {
                throw new AIResponseException(
                    request,
                    $"Invalid model name in response:\n" +
                    $"   [API response] {responseBody.model}\n" +
                    $"   [config]   {this.Model}");
            }

            if (responseBody.error != null)
            {
                throw AIEngineAPIException.FromErrorInResponseBody(responseBody.error, this.EngineIdentifier);
            }

            var sb = new StringBuilder();
            string assistantMessage = responseBody.choices[0]?.message?.content;
            if (assistantMessage != null)
            {
                sb.AppendLine(assistantMessage);
            }
            string finish_reason = responseBody.choices[0]?.finish_reason;
            if (finish_reason != null)
            {
                sb.AppendLine();
                sb.AppendLine($"FinishReason: {finish_reason}");
            }
            var promptTokens = (int?)responseBody?.usage?.prompt_tokens;
            var completionTokens = (int?)responseBody?.usage?.completion_tokens;
            if (promptTokens != null && completionTokens != null)
            {
                sb.AppendLine($"PromptTokens: {promptTokens}");
                sb.AppendLine($"CompletionTokens: {completionTokens}");
            }

            var assistantMessageExtended = sb.ToString();
            context.CreateVerboseTextFile($"{verbosePrefix}-Resp.txt", assistantMessageExtended, request.ProcessStartTime);
            Console.WriteLine();
            Console.WriteLine(AddRealTime(context, request.StartOffset, assistantMessageExtended));
            Console.WriteLine();
            Console.WriteLine();

            PauseIfEnabled(this.PauseBeforeSavingResponse);

            return new AIResponse(
                request,
                assistantMessage,
                Cost.Create(
                    request.TaskId,
                    this.EngineIdentifier,
                    request.SystemParts.Concat(request.UserParts).ToArray(),
                    watch.Elapsed,
                    request.ItemsIncluded.Length,
                    0,
                    this.EstimatedCostPerInputMillionTokens,
                    this.EstimatedCostPerOutputMillionTokens,
                    inputTokens: promptTokens,
                    outputThoughtsChars: 0,
                    outputThoughtsTokens: 0,
                    outputCandidatesChars: assistantMessage.Length,
                    outputCandidatesTokens: completionTokens));
        }

        private AIResponse ProcessStreamingResponse(
            HttpClient client,
            AIRequest request,
            string requestBodyAsJson,
            SubtitleGeneratorContext context,
            string verbosePrefix)
        {
            Timer waitingTimer = null;
            var watch = Stopwatch.StartNew();

            try
            {
                var requestId = $"{request.TaskId}, #{request.Number}: {request.UpdateMessage}";
                context.DefaultProgressUpdateHandler(ToolName, requestId, $"Opening connection...");
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(client.BaseAddress, "chat/completions"))
                {
                    Content = new StringContent(requestBodyAsJson, Encoding.UTF8, "application/json")
                };

                // Start a timer that will print waiting messages
                var startTime = DateTime.Now;
                waitingTimer = new Timer(_ =>
                {
                    context.DefaultProgressUpdateHandler(ToolName, requestId, $"Waited {DateTime.Now - startTime:mm\\:ss} for 1st token...");
                }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

                context.DefaultProgressUpdateHandler(ToolName, requestId, $"Sending request...");
                var response = client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead).Result;

                if (!response.IsSuccessStatusCode)
                {
                    throw AIEngineAPIException.FromHttpStatusCode(response, this.EngineIdentifier);
                }

                var chunksReceived = new StringBuilder();
                var fullContent = new StringBuilder();
                var thoughtContent = new StringBuilder();
                var extraContent = new StringBuilder();
                string modelName = null;
                int? promptTokens = null;
                int? completionTokens = null;
                dynamic chunkError = null;
                string finish_reason = null;
                var currentLineBuffer = new StringBuilder();                

                try
                {
                    using (var stream = response.Content.ReadAsStreamAsync().Result)
                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            chunksReceived.AppendLine(line);
                            if (string.IsNullOrWhiteSpace(line))
                                continue;

                            if (line.StartsWith("data: "))
                            {
                                var data = line.Substring(6);
                                if (data == "[DONE]")
                                {
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

                                    if (chunk?.error != null)
                                    {
                                        break;
                                    }

                                    // Extract content delta
                                    if (chunk?.choices.Count > 0)
                                    {
                                        var delta = chunk.choices[0].delta;
                                        if (delta?.content != null)
                                        {
                                            string contentChunk = delta.content;

                                            // Stop the timer on first content token
                                            if (waitingTimer != null)
                                            {
                                                Console.WriteLine();
                                                waitingTimer?.Dispose();
                                                waitingTimer = null;
                                            }
                                            if (delta.extra_content?.google?.thought == true)
                                            {
                                                thoughtContent.Append(contentChunk);
                                            }
                                            else
                                            {
                                                fullContent.Append(contentChunk);
                                            }

                                            // Write to console as chunks arrive
                                            var indexOfLastNewLine = contentChunk.LastIndexOf('\n');
                                            if (indexOfLastNewLine >= 0)
                                            {
                                                currentLineBuffer.Append(contentChunk.Substring(0, indexOfLastNewLine + 1));
                                                Console.Write(AddRealTime(context, request.StartOffset, currentLineBuffer.ToString()));
                                                currentLineBuffer.Clear();
                                                currentLineBuffer.Append(contentChunk.Substring(indexOfLastNewLine + 1));
                                            }
                                            else
                                            {
                                                currentLineBuffer.Append(contentChunk);
                                            }
                                        }
                                        else if (chunk.choices[0].finish_reason != null)
                                        {
                                            finish_reason = chunk.choices[0].finish_reason.ToString();
                                        }
                                    }

                                    // Some APIs send usage information in streaming mode
                                    if (chunk.usage != null)
                                    {
                                        promptTokens = (int?)chunk.usage.prompt_tokens;
                                        completionTokens = (int?)chunk.usage.completion_tokens;
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
                    extraContent.AppendLine();
                    extraContent.AppendLine();
                    extraContent.AppendLine($"==> An exception occured while receiving the AI response: {ex.Message}");
                    context.WriteLog(ex.ToString());
                }

                if (chunkError != null)
                {
                    throw AIEngineAPIException.FromErrorInResponseBody(chunkError, this.EngineIdentifier);
                }

                Console.Write(AddRealTime(context, request.StartOffset, currentLineBuffer.ToString()));
                Console.WriteLine();

                if (finish_reason != null)
                {
                    extraContent.AppendLine();
                    extraContent.AppendLine($"FinishReason: {finish_reason}");

                }
                if (promptTokens != null && completionTokens != null)
                {
                    extraContent.AppendLine($"PromptTokens: {promptTokens}, {GetInputCost(promptTokens.Value):C}");
                    extraContent.AppendLine($"CompletionsTokens: {completionTokens}, {GetOutputCost(completionTokens.Value):C}");
                }

                Console.WriteLine(extraContent.ToString());

                string assistantMessageExtended = fullContent.ToString() + extraContent.ToString();
                context.CreateVerboseTextFile($"{verbosePrefix}-Resp.json", chunksReceived.ToString(), request.ProcessStartTime);
                context.CreateVerboseTextFile($"{verbosePrefix}-Resp.txt", thoughtContent.ToString() + "\n" + new string('*', 80) + "\n" + assistantMessageExtended, request.ProcessStartTime);

                if (ValidateModelNameInResponse && modelName != null && !string.Equals(modelName, this.Model, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AIResponseException(
                        request,
                        $"Invalid model name in response:\n" +
                        $"   [API response] {modelName}\n" +
                        $"   [config]   {this.Model}");
                }

                PauseIfEnabled(this.PauseBeforeSavingResponse);

                return new AIResponse(
                    request,
                    assistantMessageExtended,
                    Cost.Create(
                        request.TaskId,
                        this.EngineIdentifier,
                        request.SystemParts.Concat(request.UserParts).ToArray(),
                        watch.Elapsed,
                        request.ItemsIncluded?.Length ?? 1,
                        0,
                        this.EstimatedCostPerInputMillionTokens,
                        this.EstimatedCostPerOutputMillionTokens,
                        inputTokens: promptTokens,
                        outputThoughtsChars: 0,
                        outputThoughtsTokens: 0,
                        outputCandidatesChars: fullContent.Length,
                        outputCandidatesTokens: completionTokens));
            }
            finally
            {
                waitingTimer?.Dispose();
            }
        }
    }
}