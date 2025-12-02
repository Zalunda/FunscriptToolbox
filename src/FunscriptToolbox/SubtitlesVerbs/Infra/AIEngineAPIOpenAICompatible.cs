using Newtonsoft.Json;
using System;
using System.Collections.Generic;
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

        [JsonProperty(Order = 17)]
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
                    client.DefaultRequestHeaders.Add("Authorization", "Bearer " + context.GetPrivateConfig(this.APIKeyName));
                }

                var lastTimeSaved = DateTime.Now;

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
                        content = request.SystemParts.Select(ConvertPart).ToArray()
                    });
            }
            if (request.UserParts.Any())
            {
                messages.Add(
                    new
                    {
                        role = "user",
                        content = request.UserParts.Select(ConvertPart).ToArray()
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
                throw new AIRequestException(
                    request,
                    $"Invalid model name in response:\n" +
                    $"   [API response] {responseBody.model}\n" +
                    $"   [config]   {this.Model}");
            }

            if (responseBody.error != null)
            {
                throw AIEngineAPIException.FromErrorInResponseBody(responseBody.error, this.EngineIdentifier);
            }

            string assistantMessage = responseBody.choices[0]?.message?.content;
            string finish_reason = responseBody.choices[0]?.finish_reason;
            Console.WriteLine($"\n\nFinish_reason: {finish_reason}");
            context.CreateVerboseTextFile($"{verbosePrefix}-Resp.txt", assistantMessage, request.ProcessStartTime);
            if (assistantMessage == null)
            {
                throw new AIRequestException(
                    request,
                    $"Empty response receive. Finish_reason: {finish_reason}");
            }

            Console.WriteLine("\n" + AddRealTime(context, request.StartOffset, assistantMessage) + "\n\n");

            PauseIfEnabled(this.PauseBeforeSavingResponse);

            // Let the request handle the response and store the api cost
            return new AIResponse(
                request,
                assistantMessage,
                new Cost(
                    $"{this.BaseAddress},{this.Model}",
                    TimeSpan.Zero,
                    -1,
                    request.FullPrompt.Length,
                    assistantMessage.Length,
                    (int?)responseBody?.usage?.prompt_tokens,
                    (int?)responseBody?.usage?.completion_tokens,
                    (int?)responseBody?.usage?.total_tokens));
        }

        private AIResponse ProcessStreamingResponse(
            HttpClient client,
            AIRequest request,
            string requestBodyAsJson,
            SubtitleGeneratorContext context,
            string verbosePrefix)
        {
            Timer waitingTimer = null;

            try
            {
                var requestId = $"{request.TaskId}, {request.UpdateMessage}, request #{request.Number}";
                context.DefaultProgressUpdateHandler(ToolName, requestId, $"Opening connection...");
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(client.BaseAddress, "chat/completions"))
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

                var chunksReceived = new StringBuilder();
                var fullContent = new StringBuilder();
                var thoughtContent = new StringBuilder();
                string modelName = null;
                int? promptTokens = null;
                int? completionTokens = null;
                int? totalTokens = null;
                string finish_reason = null;
                bool doneReceived = false;
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

                                    if (chunk?.error != null)
                                    {
                                        throw AIEngineAPIException.FromErrorInResponseBody(chunk.error, this.EngineIdentifier);
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
                                            Console.WriteLine($"Finish_reason: {finish_reason}");
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

                    if (!doneReceived)
                    {
                        fullContent.Append($"DONE was not received in the response.  Finish_reason: {finish_reason}");
                    }
                }
                catch (AIEngineAPIException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    fullContent.AppendLine();
                    fullContent.AppendLine();
                    fullContent.AppendLine($"==> An exception occured while receiving the AI response: {ex.Message}");
                    context.WriteLog(ex.ToString());
                }

                Console.Write(AddRealTime(context, request.StartOffset, currentLineBuffer.ToString()));
                Console.WriteLine();

                string assistantMessage = fullContent.ToString();
                context.CreateVerboseTextFile($"{verbosePrefix}-Resp.json", chunksReceived.ToString(), request.ProcessStartTime);
                context.CreateVerboseTextFile($"{verbosePrefix}-Resp.txt", thoughtContent.ToString() + "\n" + new string('*', 80) + "\n" + assistantMessage, request.ProcessStartTime);

                if (ValidateModelNameInResponse && modelName != null && !string.Equals(modelName, this.Model, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AIRequestException(
                        request,
                        $"Invalid model name in response:\n" +
                        $"   [API response] {modelName}\n" +
                        $"   [config]   {this.Model}");
                }

                if (string.IsNullOrEmpty(assistantMessage))
                {
                    throw new AIRequestException(
                        request,
                        $"Empty response received. Finish_reason: {finish_reason}");
                }

                PauseIfEnabled(this.PauseBeforeSavingResponse);

                return new AIResponse(
                    request,
                    assistantMessage,
                    new Cost(
                        $"{request.TaskId}: {this.BaseAddress},{this.Model}",
                        TimeSpan.Zero,
                        -1,
                        request.FullPrompt.Length,
                        assistantMessage.Length,
                        promptTokens,
                        completionTokens,
                        totalTokens));
            }
            finally
            {
                waitingTimer?.Dispose();
            }
        }
    }
}