using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using FunscriptToolbox.SubtitlesVerbV2;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Net.Http;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    internal class TranslatorGenericOpenAIAPI : Translator
    {
        public TranslatorGenericOpenAIAPI()
        {
        }

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string BaseAddress { get; set; } = "http://localhost:10000";

        [JsonProperty(Order = 11)]
        public string Model { get; set; }

        [JsonProperty(Order = 12)]
        public string APIKeyName { get; set; }

        [JsonProperty(Order = 13)]
        public TimeSpan TimeOut { get; set; } = TimeSpan.FromSeconds(300);

        [JsonProperty(Order = 14, TypeNameHandling = TypeNameHandling.None)]
        public ExpandoObject DataExpansion { get; set; }

        [JsonProperty(Order = 15, Required = Required.Always)]
        public AIMessagesHandler MessagesHandler { get; set; }

        public override void Translate(
            SubtitleGeneratorContext context,
            string baseFilePath,
            Transcription transcription,
            Translation translation)
        {
            try
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

                var items = this.MessagesHandler.GetAllItems(transcription, context.Wipsub.SubtitlesForcedTiming, this.TranslationId);
                foreach (var request in this.MessagesHandler.CreateRequests(
                    this.TranslationId,
                    items,
                    transcription.Language,
                    translation.Language))
                {
                    dynamic data = request.Data;
                    if (this.Model != null)
                    {
                        data.model = this.Model;
                    }
                    data = Merge(request.Data, DataExpansion);

                    var json = JsonConvert.SerializeObject(data);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    Console.WriteLine($"--------------------------------");
                    Console.WriteLine(content);

                    var watch = Stopwatch.StartNew();
                    var response = client.PostAsync(
                        new Uri(client.BaseAddress, "/v1/chat/completions"),
                        content).Result;
                    string responseContent = response.Content.ReadAsStringAsync().Result;
                    watch.Stop();

                    dynamic responseObject = JsonConvert.DeserializeObject(responseContent);
                    string assistantMessage = responseObject.choices[0].message.content;
                    translation.Costs.Add(
                        new TranslationCost(
                            $"{this.BaseAddress},{this.Model}",
                            watch.Elapsed,
                            request.Items.Length,
                            (int?)responseObject.usage?.prompt_tokens,
                            (int?)responseObject.usage?.completion_tokens));
                    this.MessagesHandler.HandleResponse(
                        translation.Id,
                        items,
                        assistantMessage);
                    context.Wipsub.Save();
                }
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
                // TODO 
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