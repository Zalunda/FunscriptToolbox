using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    internal class TranslatorGoogleV1API : Translator
    {
        private const string ToolName = "GoogleV1-API";

        public TranslatorGoogleV1API()
        {
        }

        [JsonProperty(Order = 20, Required = Required.Always)]
        public string TranscriptionId { get; set; }
        [JsonProperty(Order = 21, Required = Required.Always)]
        public string MetadataNeeded { get; set; }
        [JsonProperty(Order = 22, Required = Required.Always)]
        public string MetadataProduced { get; set; }

        protected override string GetMetadataProduced() => this.MetadataProduced;

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (context.WIP.Transcriptions.FirstOrDefault(f => f.Id == TranscriptionId && f.IsFinished) == null)
            {
                reason = $"Transcription '{this.TranscriptionId}' is not done yet.";
                return false;
            }

            reason = null;
            return true;
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Translation translation)
        {
            var transcription = context.WIP.Transcriptions.FirstOrDefault(f => f.Id == TranscriptionId && f.IsFinished);

            var missingTranscriptions = transcription.Items
                .Where(transcribedItem => !translation.Items.Any(x => x.StartTime == transcribedItem.StartTime))
                .ToArray();

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
            client.BaseAddress = new Uri("https://translate.googleapis.com/");

            var watch = Stopwatch.StartNew();
            var nbAdded = 0;
            try
            {
                var currentIndex = 1;
                var sourceLanguage = transcription.Language?.ShortName ?? "auto";
                int maxConcurrency = 5; // The maximum number of requests running at any given time

                // Local helper method to start a background HTTP request task
                Task<(bool IsSuccess, string Text, string Error)> StartTask(TranscribedItem transcribedItem)
                {
                    return Task.Run(() =>
                    {
                        try
                        {
                            string apiUrl = $"https://translate.googleapis.com/translate_a/single" +
                                "?client=gtx" +
                                $"&sl={sourceLanguage}" +
                                $"&tl={translation.Language.ShortName}" +
                                $"&dt=t" +
                                $"&q={Uri.EscapeDataString(transcribedItem.Metadata.Get(this.MetadataNeeded) ?? string.Empty)}";

                            var response = GetWithRetry(client, apiUrl);
                            if (!response.IsSuccessStatusCode)
                            {
                                return (false, (string)null, $"Error: {response.StatusCode} - {response.ReasonPhrase}");
                            }

                            string responseAsJson = response.Content.ReadAsStringAsync().Result;
                            dynamic responseBody = JsonConvert.DeserializeObject(responseAsJson);
                            var translatedText = (string)ExtractTranslatedText(responseBody);

                            return (true, translatedText, (string)null);
                        }
                        catch (Exception ex)
                        {
                            return (false, (string)null, $"Exception: {ex.Message}");
                        }
                    });
                }

                // A queue to hold our running tasks in exact chronological order
                var runningTasks = new Queue<(TranscribedItem Item, Task<(bool IsSuccess, string Text, string Error)> Task)>();

                int missingIndex = 0;

                // 1. Fill the initial pipeline with up to 'maxConcurrency' tasks
                while (runningTasks.Count < maxConcurrency && missingIndex < missingTranscriptions.Length)
                {
                    var item = missingTranscriptions[missingIndex++];
                    runningTasks.Enqueue((item, StartTask(item)));
                }

                // 2. Process tasks sequentially. As one finishes, enqueue a new one.
                while (runningTasks.Count > 0)
                {
                    // Dequeue the oldest item to guarantee sequential, ordered writing
                    var (transcribedItem, task) = runningTasks.Dequeue();

                    // Block and wait ONLY for this specific oldest task to finish
                    var result = task.Result;

                    // If an error occurred, log it and stop. 
                    // The 'finally' block below guarantees previously succeeded items are saved!
                    if (!result.IsSuccess)
                    {
                        context.WriteError(result.Error);
                        return;
                    }

                    // Write the successful translation to our list
                    translation.Items.Add(
                        new TranslatedItem(
                            transcribedItem.StartTime,
                            transcribedItem.EndTime,
                            MetadataCollection.CreateSimple(this.MetadataProduced, result.Text)));

                    nbAdded++;
                    context.DefaultUpdateHandler(ToolName, $"{currentIndex++}/{missingTranscriptions.Length}", result.Text);

                    // 3. Because we just finished one, start exactly ONE new task to replace it (if any are left)
                    if (missingIndex < missingTranscriptions.Length)
                    {
                        var nextItem = missingTranscriptions[missingIndex++];
                        runningTasks.Enqueue((nextItem, StartTask(nextItem)));
                    }
                }

                translation.MarkAsFinished();
            }
            finally
            {
                watch.Stop();
                translation.Costs.Add(
                    new Cost(transcription.Id, ToolName, watch.Elapsed, nbAdded));
                context.WIP.Save();
            }
        }
        public static Language DetectLanguage(IEnumerable<TranscribedItem> items, string metadataNeeded)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
            client.BaseAddress = new Uri("https://translate.googleapis.com/");

            var guesses = new Dictionary<string, int>();
            var nbOccurencesToBeSure = 5;
            foreach (var transcribedItem in items ?? Array.Empty<TranscribedItem>())
            {
                string apiUrl = $"https://translate.googleapis.com/translate_a/single" +
                    "?client=gtx" +
                    $"&sl=auto" +
                    $"&tl=en" +
                    $"&dt=t" +
                    $"&q={Uri.EscapeDataString(transcribedItem.Metadata.Get(metadataNeeded))}";

                // Call the API with Retry Logic
                var response = GetWithRetry(client, apiUrl);
                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Can't detect language using Google API. Error: {response.StatusCode} - {response.ReasonPhrase}");
                }

                string responseAsJson = response.Content.ReadAsStringAsync().Result;

                dynamic responseBody = JsonConvert.DeserializeObject(responseAsJson);
                string detectedSourceLanguage = (string)responseBody[2];

                if (!guesses.TryGetValue(detectedSourceLanguage, out var occurence))
                {
                    occurence = 1;
                }
                else
                {
                    occurence++;
                }
                guesses[detectedSourceLanguage] = occurence;

                if (occurence >= nbOccurencesToBeSure)
                {
                    return Language.FromString(detectedSourceLanguage);
                }
            }

            var bestGuess = guesses
                .OrderByDescending(item => item.Value)
                .Select(item => item.Key)
                .FirstOrDefault()
                ?? "ja";
            return Language.FromString(bestGuess);
        }

        public static string SimpleTranslate(
            string originalText,
            string shortOriginalLanguage,
            string shortTranslationLanguage)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 6.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2228.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Type", "application/json; charset=UTF-8");
            client.BaseAddress = new Uri("https://translate.googleapis.com/");

            string apiUrl = $"https://translate.googleapis.com/translate_a/single" +
                "?client=gtx" +
                $"&sl={shortOriginalLanguage}" +
                $"&tl={shortTranslationLanguage}" +
                $"&dt=t" +
                $"&q={Uri.EscapeDataString(originalText)}";

            // Call the API with Retry Logic
            var response = GetWithRetry(client, apiUrl);
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Error: {response.StatusCode} - {response.ReasonPhrase}");
            }

            string responseAsJson = response.Content.ReadAsStringAsync().Result;
            dynamic responseBody = JsonConvert.DeserializeObject(responseAsJson);

            return (string)ExtractTranslatedText(responseBody);
        }

        private static HttpResponseMessage GetWithRetry(HttpClient client, string apiUrl, int maxRetries = 3)
        {
            int delayMs = 1000; // Start with a 1 second delay
            int attempts = 0;

            while (true)
            {
                attempts++;
                try
                {
                    var response = client.GetAsync(apiUrl).Result;

                    // If successful OR it's the very last attempt, return the response.
                    // (If it failed on the last attempt, the caller will log the StatusCode)
                    if (response.IsSuccessStatusCode || attempts == maxRetries)
                    {
                        return response;
                    }

                    // Dispose the failed response to prevent memory leaks before retrying
                    response.Dispose();
                }
                catch (Exception)
                {
                    // If an actual network Exception occurs on the last attempt, re-throw the exact exception
                    if (attempts >= maxRetries)
                    {
                        throw;
                    }
                }

                // Pause thread before retrying (Exponential Backoff: 1s, 2s, 4s...)
                Thread.Sleep(delayMs);
                delayMs *= 2;
            }
        }

        private static string ExtractTranslatedText(dynamic result)
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