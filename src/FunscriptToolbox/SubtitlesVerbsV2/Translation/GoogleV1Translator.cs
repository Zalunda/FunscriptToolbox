using System;
using System.Net.Http;
using Newtonsoft.Json;
using System.Linq;
using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using System.Collections.Generic;
using System.Diagnostics;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    internal class GoogleV1Translator : Translator
    {
        public GoogleV1Translator(
            string translationId)
            : base(translationId)
        {
        }

        public override void Translate(
            string baseFilePath,
            Transcription transcription,
            Translation translation,
            Action saveAction)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
            client.BaseAddress = new Uri("https://translate.googleapis.com/");

            var costsAsList = new List<TranslationCost>();
            foreach (var transcribedText in transcription
                .Items
                .Where(f => !f.TranslatedTexts.Any(t => t.Id == this.TranslationId)))
            {
                string apiUrl = $"https://translate.googleapis.com/translate_a/single" + 
                    "?client=gtx" + 
                    $"&sl={transcription.Language.ShortName}" +
                    $"&tl={translation.Language.ShortName}" + 
                    $"&dt=t" + 
                    $"&q={Uri.EscapeDataString(transcribedText.Text)}";

                var watch = Stopwatch.StartNew();
                HttpResponseMessage response = client.GetAsync(apiUrl).Result;
                if (response.IsSuccessStatusCode)
                {
                    string jsonResponse = response.Content.ReadAsStringAsync().Result;
                    dynamic result = JsonConvert.DeserializeObject(jsonResponse);
                    var translatedText = (string)ExtractTranslatedText(result);
                    transcribedText.TranslatedTexts.Add(
                        new TranslatedText(translation.Id, translatedText));

                    translation.Costs.Add(
                        new TranslationCost("GoogleV1", 1, watch.Elapsed));
                }
                else
                {
                    throw new HttpRequestException($"Error: {response.StatusCode} - {response.ReasonPhrase}");
                }
            }

            saveAction();
        }

        private string ExtractTranslatedText(dynamic result)
        {
            var translatedText = "";
            foreach (var item in result[0])
            {
                if (translatedText.Length > 0)
                    translatedText += " ";
                translatedText += item[0].Value;
            }
            return translatedText;
        }
    }
}
