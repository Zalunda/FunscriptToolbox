using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using FunscriptToolbox.SubtitlesVerbV2;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    internal class TranslatorAIGenericAPI : TranslatorAI
    {
        public const string ToolName = "API";

        public TranslatorAIGenericAPI()
        {
        }

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
        public ExpandoObject DataExpansion { get; set; }

        [JsonProperty(Order = 16)]
        public int? DebugNbRequestsLimit { get; set; } = null;

        public override void Translate(
            SubtitleGeneratorContext context,
            Transcription transcription,
            Translation translation)
        {
            // Get only the items that have not been translated yet
            var items = this.MessagesHandler.GetAllItems(
                transcription,
                context.Wipsub.SubtitlesForcedTiming);

            // Parse previous files, they might contains translations if the user fixed them
            var nbErrors = this.HandlePreviousFiles(
                context,
                transcription,
                translation,
                items,                
                $"-BATCH-\\d+\\.txt");

            // If there are still translations to be done, create files for each batch of items
            if (nbErrors == 0)
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
                client.BaseAddress = new Uri(this.BaseAddress);
                client.Timeout = this.TimeOut;
                if (this.APIKeyName != null)
                {
                    client.DefaultRequestHeaders.Add("Authorization", "Bearer " + context.GetPrivateConfig(this.APIKeyName));
                }

                var verboseFileId = DateTime.Now.ToString("yyyyMMddHHmmss");
                var lastTimeSaved = DateTime.Now;
                foreach (var request in this.MessagesHandler.CreateRequests(
                    transcription,
                    translation,
                    items))
                {
                    if (DebugNbRequestsLimit != null && request.Number > DebugNbRequestsLimit.Value)
                    {
                        context.WriteInfo($"    Stopping because DebugNbRequestsLimit reached ({DebugNbRequestsLimit.Value})");
                        return;
                    }

                    dynamic data = request.Data;
                    if (this.Model != null)
                    {
                        data.model = this.Model;
                    }
                    data = Merge(request.Data, DataExpansion);

                    var requestJson = JsonConvert.SerializeObject(data, Formatting.Indented);

                    var verboseRequestPrefix = $"{context.BaseFilePath}.{transcription.Id}-{translation.Id}-{verboseFileId}-{request.Number:D04}";
                    context.CreateVerboseFile(verboseRequestPrefix + "-Req.json", requestJson);

                    var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                    try
                    {
                        var watch = Stopwatch.StartNew();
                        var response = client.PostAsync(
                            new Uri(client.BaseAddress, "/v1/chat/completions"),
                            content).Result;
                        string responseJson = response.Content.ReadAsStringAsync().Result;
                        watch.Stop();

                        dynamic responseObject = JsonConvert.DeserializeObject(responseJson);
                        context.CreateVerboseFile(verboseRequestPrefix + "-Resp.json", JsonConvert.SerializeObject(responseObject, Formatting.Indented));

                        string assistantMessage = responseObject.choices[0].message.content;
                        translation.Costs.Add(
                            new TranslationCost(
                                $"{this.BaseAddress},{this.Model}",
                                watch.Elapsed,
                                request.Items.Length,
                                (int?)responseObject.usage?.prompt_tokens,
                                (int?)responseObject.usage?.completion_tokens));

                        if (ValidateModelNameInResponse && !string.Equals((string)responseObject.model, this.Model, StringComparison.OrdinalIgnoreCase))
                        {
                            context.WriteError($"Invalid model name in response:");
                            context.WriteError($"   [response] {responseObject.model}");
                            context.WriteError($"   [config]   {this.Model}");
                            return;
                        }

                        var nbTranslationAdded = this.MessagesHandler.HandleResponse(
                            translation,
                            items,
                            assistantMessage,
                            request.Items);
                        if (DateTime.Now - lastTimeSaved > TimeSpan.FromMinutes(1))
                        {
                            context.Wipsub.Save();
                            lastTimeSaved = DateTime.Now;
                        }

                        context.DefaultUpdateHandler(
                            ToolName, 
                            request.ToolAction, 
                            request.Items.LastOrDefault()?.Tag.TranslatedTexts.FirstOrDefault(f => f.Id == translation.Id)?.Text); // Display the last translated text
                    }
                    catch (AIMessagesHandlerExpection ex)
                    {
                        var filepath = $"{context.BaseFilePath}.{transcription.Id}-{translation.Id}-BATCH-{request.Number:D04}.txt";
                        File.WriteAllText(filepath, ex.PartiallyFixedResponse, Encoding.UTF8);
                        context.WriteInfo($"Error while parsing response from the API: {ex.Message}");
                        context.AddUserTodo($"Manually fix the following error in file '{filepath}':\n{ex.Message}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        context.WriteError($"Error while parsing response from the API: {ex.Message}");
                        context.Wipsub.Save();
                        return;
                    }
                }
            }
        }

        private static dynamic Merge(ExpandoObject item1, ExpandoObject item2)
        {
            if (item2 == null)
                return item1;

            var result = new ExpandoObject();
            var d = result as IDictionary<string, object>; //work with the Expando as a Dictionary

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