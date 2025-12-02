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
    public class AIEngineAPIGoogle : AIEngineAPI
    {
        public override string ToolName { get; } = "Google-API";

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

                var lastTimeSaved = DateTime.Now;

                dynamic requestBody = CreateRequestBody(request);
                var requestBodyAsJson = JsonConvert.SerializeObject(requestBody, Formatting.Indented);

                var verbosePrefix = request.GetVerbosePrefix();
                context.CreateVerboseTextFile($"{verbosePrefix}-Req.txt", $"Base Adress: {this.BaseAddress}\nModel: {this.Model}\n\n{request.FullPrompt}", request.ProcessStartTime);
                context.CreateVerboseTextFile($"{verbosePrefix}-Req.json", requestBodyAsJson, request.ProcessStartTime);

                PauseIfEnabled(this.PauseBeforeSendingRequest, request.FullPrompt);

                return ProcessStreamingResponse(client, request, requestBodyAsJson, context, verbosePrefix);
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
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(client.BaseAddress, $"models/{this.Model}:streamGenerateContent?key={context.GetPrivateConfig(this.APIKeyName)}"))
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
                var fullContent = new StringBuilder();
                var thoughtContent = new StringBuilder();
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

                                var googleEvent = serializer.Deserialize<GoogleEvent>(new JsonTextReader(new StringReader(chunkAsString)));

                                foreach (var part in googleEvent.GetParts())
                                {
                                    if (part.Thought == true)
                                    {
                                        thoughtContent.Append(part.Text);
                                    }
                                    else
                                    {
                                        fullContent.Append(part.Text);
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
                                    currentLineBuffer.AppendLine($"\n\nFinishReason: {candidate.FinishReason}\nFinishMessage: {candidate.FinishMessage}");
                                }

                                googleUsageMetadata = googleEvent.UsageMetadata ?? googleUsageMetadata;
                            }
                        }
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

                if (googleUsageMetadata != null)
                {
                    currentLineBuffer.AppendLine($"PromptTokenCount: {googleUsageMetadata.PromptTokenCount}");
                    currentLineBuffer.AppendLine($"ThoughtsTokenCount: {googleUsageMetadata.ThoughtsTokenCount}");
                    currentLineBuffer.AppendLine($"CandidatesTokenCount: {googleUsageMetadata.CandidatesTokenCount}");
                }

                Console.Write(AddRealTime(context, request.StartOffset, currentLineBuffer.ToString()));
                Console.WriteLine();

                string assistantMessage = fullContent.ToString();
                context.CreateVerboseTextFile($"{verbosePrefix}-Resp.json", chunksReceived.ToString(), request.ProcessStartTime);
                context.CreateVerboseTextFile($"{verbosePrefix}-Resp.txt", thoughtContent.ToString() + "\n" + new string('*', 80) + "\n" + assistantMessage, request.ProcessStartTime);

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
                        googleUsageMetadata?.PromptTokenCount,
                        googleUsageMetadata?.CandidatesTokenCount + googleUsageMetadata?.ThoughtsTokenCount,
                        googleUsageMetadata?.TotalTokenCount));
            }
            finally
            {
                waitingTimer?.Dispose();
            }
        }

        private class GoogleEvent
        {
            public GoogleCandidate[] Candidates { get; set; }
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
            public int CandidatesTokenCount { get; set; }
            public int TotalTokenCount { get; set; }
            public int ThoughtsTokenCount { get; set; }
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