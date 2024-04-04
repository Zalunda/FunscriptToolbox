using System;
using System.Net.Http;
using Newtonsoft.Json;
using System.Linq;
using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using System.Collections.Generic;
using System.Diagnostics;
using FunscriptToolbox.SubtitlesVerbV2;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    internal class TranslatorDeepLAPI : Translator
    {
        private const string ToolName = "DeepL-API";

        public TranslatorDeepLAPI()
        {
        }

        [JsonProperty(Order = 11)]
        public string APIKeyName { get; set; } = "DeepLAPIKey";

        [JsonProperty(Order = 12)]
        public int NbPerRequest { get; set; } = 20;

        public override void Translate(
            SubtitleGeneratorContext context,
            Transcription transcription,
            Translation translation)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
            client.BaseAddress = new Uri("https://api.deepl.com/");

            var costsAsList = new List<TranslationCost>();
            var missingTranscriptions = transcription
                .Items
                .Where(f => !f.TranslatedTexts.Any(t => t.Id == translation.Id))
                .ToArray();

            var currentStartIndex = 0;
            while (currentStartIndex < missingTranscriptions.Length)
            {
                var batch = missingTranscriptions
                    .Skip(currentStartIndex)
                    .Take(NbPerRequest)
                    .ToArray();

                var requestBody = new
                {
                    auth_key = context.GetPrivateConfig(this.APIKeyName),
                    text = batch.Select(f => f.Text).ToArray(),
                    target_lang = translation.Language.ShortName
                };

                string requestBodyJson = JsonConvert.SerializeObject(requestBody);
                string apiUrl = $"https://api.deepl.com/v2/translate";

                var watch = Stopwatch.StartNew();
                var response = client.GetAsync(apiUrl).Result;
                if (response.IsSuccessStatusCode)
                {
                    translation.Costs.Add(
                        new TranslationCost(ToolName, watch.Elapsed, batch.Length));

                    string jsonResponse = response.Content.ReadAsStringAsync().Result;
                    dynamic jsonObject = JsonConvert.DeserializeObject(jsonResponse);
                    var index = 0;
                    foreach (string translatedText in jsonObject.translations)
                    {
                        batch[index].TranslatedTexts.Add(
                            new TranslatedText(translation.Id, translatedText));
                        index++;
                        context.DefaultUpdateHandler(ToolName, $"{currentStartIndex + index}/{missingTranscriptions.Length}", translatedText);
                    }
                }
                else
                {
                    context.WriteError($"Error: {response.StatusCode} - {response.ReasonPhrase}");
                    return;
                }

                currentStartIndex += NbPerRequest;
            }
        }
    }
}
