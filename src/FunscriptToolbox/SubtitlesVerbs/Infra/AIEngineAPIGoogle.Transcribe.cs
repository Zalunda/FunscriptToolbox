using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    // =================================================================================================================================
    // NOTES FOR LATER (2026-11-26): the state of the tests of 'gemini-3.5-transcribe', the open questions and the possible improvements.
    // (used by TranscriberAudioFullAIGoogleTranscribe, step 'full-transcribe' of the main configuration, disabled for now: 'full-ai' is
    //  still a TranscriberAudioFullAI. If it replaces the type of 'full-ai' (same id), what scenes already did is kept, see its JobState.)
    //
    // What was measured
    //   - MDVR-094 (15 min, the first part), against the timings reviewed by hand: word starts ~+0.1 s (85 % within 0.2 s), word ends
    //     ~0.2 s too early (=> EndPadding 0.25 s). The times are in steps of 0.1 s; ~25 % of the words last <= 0.1 s, some 0 s.
    //     (careful: the texts of a manual-input file can be on the neighbour subtitle; compare the boundaries, not the texts)
    //   - The text is put on the right subtitle (better than 'full-ai', whose texts were often on the neighbour in that part).
    //   - Cost: 25 audio tokens / s ($2 / M) + ~900 output tokens / 15 min ($12 / M): ~$0.21 / hour. Diarization: ~+4 % output, nothing.
    //   - Fast: ~13 s for 15 min of audio.
    //   - MDVR-093 (52 min, 4 women): 25-min chunks: the 2nd chunk had broken times at its start, lost the speakers after 2 min (955 of
    //     1,186 words without speaker) and ended 'incomplete' with only 1,688 output tokens (not the token limit?). 5-min chunks: no lost
    //     speaker, 1 broken word, more words (4,389 vs 3,625), the same cost. => Shorter chunks look better (default 25 min: change to 5?).
    //
    // Open questions
    //   - 'incomplete': what is the real reason (incomplete_details was not captured: run with --verbose to get the -Resp.json)? Is there
    //     a maximum of output tokens to raise, and what is its name for this API ('generation_config.max_output_tokens'?, can be set with
    //     RequestBodyExtension, it's merged with transcription_config)? The answer is used up to its last subtitle, see the transcriber.
    //   - The statuses of an interaction still being done ('in_progress', 'queued'... rs_transcribeInProgressStatuses): guessed, never seen
    //     (the answers were always 'completed' / 'incomplete' right away).
    //   - The rate limit: 3 requests in less than a minute => 'Too many requests'. The real limit (by minute? by day? by tier?) is not known:
    //     TranscribeMinTimeBetweenRequests 30 s is a guess.
    //   - A chunk that comes back almost empty: MDVR-093 48:21-52:03 (3 min 42 s) gave only 'うん。 ねえ。' ('completed'), where a 25-min
    //     chunk had 48 subtitles. Censorship of explicit content? Something else?
    //   - language_codes ["ja-JP"] (from SourceLanguage) vs [] (detected): not compared.
    //   - 'smart' mode (text only, cleaner): cut after the first 20 s of the 15 min (31 output tokens) in the only test. Not used.
    //   - custom_vocabulary (names of the characters...): can't be used with the word times / diarization (refused by the API).
    //   - The audio is uploaded with the Files API (then deleted); the inline base64 audio was not tried (size limit?).
    //
    // Possible improvements
    //   - A chunk with far fewer words / minute than the others (ex. < 20 % of the average of the scene): asked again once automatically,
    //     then flagged in the log if it's still so.
    //   - 'Forget the answer of one chunk' (redo only that part), instead of 'Redo this step' on the whole transcription.
    //   - The same line returned twice is merged (same text, starts <= SameLineMaxStartDifference): also the nearly same text (one contains
    //     the other, or >= ~80 % similar, ex. 「あ、私も見てもらっていいですか。」 / 「うんあ、私も見てもらっていいですか。」).
    //   - Lines of different speakers that overlap (different texts): most of them should become one subtitle with {Speaker:Multiple}
    //     (like the old prompt did, '- line 1\n- line 2'?). The rule is to be decided on examples (MDVR-093 has ~30 of them, ex. 14:12).
    //   - Diarization can't identify the characters (women with similar voices: 4 women on 2 numbers; the numbers change from a chunk to
    //     the next): it's only useful to cut the subtitles when the speaker changes and to detect Multiple.
    //   - The broken word times (seen at the start of some chunks: end before start, 16-minute words, starts far in the future): removed by
    //     TranscriberAudioFullAIGoogleTranscribe.RemoveBrokenWords (thresholds: 5 s, 30 s later than the next 20 words): to keep an eye on.
    //   - The subtitles built from the words (MaxGap 0.6 s, 。？！, MaxDuration 6 s, MaxChars 28): to tune on reviewed scenes (CanBeUpdated:
    //     rebuilt from the answers kept, without a new request).
    //   - If it becomes the source of the manual-input file: compare the Review editor work needed vs 'full-ai'; much of the timing refiner
    //     (RefineTiming engines, their prompts) may become useless.
    // =================================================================================================================================

    /// <summary>
    /// The transcription models of Google (ex. 'gemini-3.5-transcribe'): not 'generateContent', the Interactions API
    /// ('POST {BaseAddress}/interactions'). The audio is uploaded first with the Files API ('{host}/upload/{version}/files'),
    /// then deleted. Verbatim transcription, with the start / end of each word (and its speaker, with diarization).
    /// No batch mode for these models: the engine must have 'UseBatchMode: false'.
    /// </summary>
    public partial class AIEngineAPIGoogle
    {
        [ConfigDoc("Transcription models only: at least this time between two requests to this engine, all the scenes together (the rate limit of Google is by key).",
            Advice = "The rate limit of the transcription model is low ('Too many requests' after 3 requests in less than a minute): 30 s is safe.")]
        [JsonProperty(Order = 34)]
        public TimeSpan TranscribeMinTimeBetweenRequests { get; set; } = TimeSpan.Zero;

        [ConfigDoc("Transcription models only: on 'Too many requests' (HTTP 429), the request is sent again after a wait (30 s, then twice as long each time, at most 10 minutes), at most this number of times. 0: an error right away.")]
        [JsonProperty(Order = 35)]
        public int TranscribeMaxRetriesOnTooManyRequests { get; set; } = 5;

        /// <summary>The statuses of an interaction still being done (asked again); any other one is the end.</summary>
        private static readonly HashSet<string> rs_transcribeInProgressStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "in_progress", "queued", "pending", "processing", "running"
        };

        /// <summary>The time of the last transcription request (reserved), by engine (EngineIdentifier).</summary>
        private static readonly Dictionary<string, DateTime> rs_lastTranscribeRequestByEngine = new Dictionary<string, DateTime>();

        public class TranscribeOptions
        {
            /// <summary>BCP-47 codes (ex. "ja-JP"); empty: detected by the model.</summary>
            public string[] LanguageCodes { get; set; } = Array.Empty<string>();

            /// <summary>The speaker of each word ("spk:0", "spk:1"...), at most 8 speakers.</summary>
            public bool Diarization { get; set; }
        }

        public class TranscribedWordInfo
        {
            public TimeSpan StartTime { get; set; }
            public TimeSpan EndTime { get; set; }
            public string Text { get; set; }
            public string Speaker { get; set; }
            /// <summary>Where the word is in the text (UTF-8 bytes, as sent by the API; null: not sent).</summary>
            public int? StartIndex { get; set; }
            public int? EndIndex { get; set; }
        }

        public class TranscribeResult
        {
            public AIRequest Request { get; set; }
            public string Text { get; set; }
            public TranscribedWordInfo[] Words { get; set; }
            public Cost Cost { get; set; }
            /// <summary>The answer was cut (status 'incomplete', ex. the maximum of output tokens): why (null: complete).</summary>
            public string IncompleteReason { get; set; }
        }

        /// <param name="request">Its user parts: one audio part (the audio to transcribe; its times are relative to its start).</param>
        public TranscribeResult Transcribe(
            SubtitleGeneratorContext context,
            AIRequest request,
            TranscribeOptions options,
            AIEngineConfigOverride configOverride = null)
        {
            if (this.UseBatchMode)
            {
                throw new Exception($"The engine '{this.EngineIdentifier}' is used for a transcription (model '{this.Model}'): the transcription models have no batch mode. Set 'UseBatchMode' to false on this engine.");
            }
            var audio = request.UserParts.OfType<AIRequestPartAudio>().ToArray();
            if (audio.Length != 1)
            {
                throw new Exception($"A transcription request needs exactly one audio part (received: {audio.Length}).");
            }

            using var localResourceLock = AcquireLocalResourceLockIfNeeded(context);
            var verbosePrefix = request.GetVerbosePrefix();
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.TryAddWithoutValidation("x-goog-api-key", context.GetValidatedPrivateConfig(this.APIKeyName));
                client.Timeout = this.TimeOut;
                var baseUri = new Uri(this.BaseAddress.EndsWith("/") ? this.BaseAddress : this.BaseAddress + "/");
                PauseIfEnabled(context, this.PauseBeforeSendingRequest, $"Transcription of {audio[0].FileName} ({audio[0].Duration:hh\\:mm\\:ss}) with '{this.Model}'.");
                return RunStoppable(context, client, () =>
                {
                    for (var attempt = 1; ; attempt++)
                    {
                        WaitForTranscribeSlot(context);
                        try
                        {
                            return TranscribeInternal(context, client, baseUri, request, audio[0], options, configOverride, verbosePrefix);
                        }
                        catch (AIEngineAPIException ex) when (ex.ErrorType == AIEngineErrorType.QuotasUsed && attempt <= this.TranscribeMaxRetriesOnTooManyRequests)
                        {
                            // ('Too many requests': sent again after a wait, longer each time)
                            var delay = TimeSpan.FromSeconds(Math.Min(600, 30 * Math.Pow(2, attempt - 1)));
                            context.WriteInfo($"Too many requests (attempt {attempt}/{this.TranscribeMaxRetriesOnTooManyRequests + 1}): sent again in {delay.TotalSeconds:0} s.");
                            context.WriteLog(ex.ToString());
                            WaitStoppable(delay, remaining => context.AIRequestProgressUpdateHandler(ToolName, $"Too many requests: sent again in {remaining.TotalSeconds:0} s..."));
                        }
                    }
                });
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

        private TranscribeResult TranscribeInternal(
            SubtitleGeneratorContext context,
            HttpClient client,
            Uri baseUri,
            AIRequest request,
            AIRequestPartAudio audio,
            TranscribeOptions options,
            AIEngineConfigOverride configOverride,
            string verbosePrefix)
        {
            var watch = Stopwatch.StartNew();
            string fileName = null;
            try
            {
                // 1. The audio, uploaded (Files API: '{host}/upload/{version of BaseAddress}/files', resumable, in one go)
                context.AIRequestProgressUpdateHandler(ToolName, $"Uploading the audio ({audio.Content.Length / 1_000_000.0:0.0} MB)...");
                var version = baseUri.AbsolutePath.Trim('/');
                var uploadStart = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, $"/upload/{version}/files"))
                {
                    Content = new StringContent(JsonConvert.SerializeObject(new { file = new { display_name = audio.FileName } }), Encoding.UTF8, "application/json")
                };
                uploadStart.Headers.Add("X-Goog-Upload-Protocol", "resumable");
                uploadStart.Headers.Add("X-Goog-Upload-Command", "start");
                uploadStart.Headers.Add("X-Goog-Upload-Header-Content-Length", audio.Content.Length.ToString(CultureInfo.InvariantCulture));
                uploadStart.Headers.Add("X-Goog-Upload-Header-Content-Type", "audio/wav");
                var startResponse = Send(client, uploadStart);
                if (!startResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var uploadUrls))
                {
                    throw new Exception($"The upload of the audio was not accepted (no 'X-Goog-Upload-URL' in the answer): {startResponse.Content.ReadAsStringAsync().Result}");
                }
                var upload = new HttpRequestMessage(HttpMethod.Post, uploadUrls.First()) { Content = new ByteArrayContent(audio.Content) };
                upload.Headers.Add("X-Goog-Upload-Offset", "0");
                upload.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
                var file = (JObject)JObject.Parse(Send(client, upload).Content.ReadAsStringAsync().Result)["file"];
                fileName = (string)file["name"];
                // (ready when its state is not 'PROCESSING' anymore)
                while ((string)file["state"] == "PROCESSING")
                {
                    context.AIRequestProgressUpdateHandler(ToolName, "The uploaded audio is being processed by Google...");
                    Thread.Sleep(2000);
                    file = JObject.Parse(Send(client, new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, fileName))).Content.ReadAsStringAsync().Result);
                }
                if ((string)file["state"] == "FAILED")
                {
                    throw new Exception($"The uploaded audio can't be used by Google: {file}");
                }

                // 2. The transcription (verbatim, the times of the words; the speakers with diarization)
                dynamic requestBody = base.CreateRequestBodyWithExtension(configOverride);
                requestBody.model = $"models/{this.Model}";
                requestBody.input = new object[] { new { type = "audio", uri = (string)file["uri"], mime_type = "audio/wav" } };
                dynamic mode = new ExpandoObject();
                mode.type = "verbatim";
                mode.timestamp_granularities = new[] { "word" };
                if (options.Diarization)
                    mode.diarization_mode = "speaker";
                // (in the 'generation_config' of RequestBodyExtension, if any: ex. "max_output_tokens")
                var body = (IDictionary<string, object>)requestBody;
                var generationConfig = body.TryGetValue("generation_config", out var existing) && existing is IDictionary<string, object> existingConfig
                    ? existingConfig
                    : new ExpandoObject();
                generationConfig["transcription_config"] = new
                {
                    language_codes = options.LanguageCodes ?? Array.Empty<string>(),
                    mode
                };
                body["generation_config"] = generationConfig;
                var requestBodyAsJson = JsonConvert.SerializeObject(requestBody, Formatting.Indented);
                context.CreateVerboseTextFile($"{verbosePrefix}-Req.json", requestBodyAsJson, request.ProcessStartTime);

                context.AIRequestProgressUpdateHandler(ToolName, $"Transcribing {audio.Duration:hh\\:mm\\:ss} of audio...");
                var answer = JObject.Parse(Send(client, new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "interactions"))
                {
                    Content = new StringContent(requestBodyAsJson, Encoding.UTF8, "application/json")
                }).Content.ReadAsStringAsync().Result);
                // (still working: asked again until it's done; any other status is the end: 'completed', 'incomplete' (cut), an error...)
                while ((string)answer["status"] is string status && rs_transcribeInProgressStatuses.Contains(status))
                {
                    context.AIRequestProgressUpdateHandler(ToolName, $"Transcribing ({status})...");
                    Thread.Sleep(3000);
                    var id = (string)answer["id"];
                    var name = id.StartsWith("interactions/") ? id : "interactions/" + id;
                    answer = JObject.Parse(Send(client, new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, name))).Content.ReadAsStringAsync().Result);
                }
                context.CreateVerboseTextFile($"{verbosePrefix}-Resp.json", answer.ToString(Formatting.Indented), request.ProcessStartTime);
                var finalStatus = (string)answer["status"];
                string incompleteReason = null;
                if (finalStatus == "incomplete")
                {
                    // (the answer was cut, ex. the maximum of output tokens: what was received is used, the rest of the chunk is asked again)
                    incompleteReason = (answer["incomplete_details"] ?? answer["status_details"] ?? answer["error"])?.ToString(Formatting.None) ?? "no reason given";
                    context.WriteInfo($"The transcription is incomplete (the answer was cut): {incompleteReason}.", ConsoleColor.Yellow);
                }
                else if (finalStatus != "completed")
                {
                    throw new Exception($"The transcription ended with the status '{finalStatus}': {answer["error"] ?? answer}");
                }

                // 3. The text and the words
                var text = new StringBuilder();
                var words = new List<TranscribedWordInfo>();
                foreach (var step in answer["steps"] ?? new JArray())
                {
                    // (only what the model produced)
                    if ((string)step["type"] != "model_output")
                        continue;
                    foreach (var content in step["content"] ?? new JArray())
                    {
                        if ((string)content["type"] != "text")
                            continue;
                        var textOffset = Encoding.UTF8.GetByteCount(text.ToString());
                        text.Append((string)content["text"]);
                        foreach (var annotation in content["annotations"] ?? new JArray())
                        {
                            if ((string)annotation["type"] != "word_info")
                                continue;
                            words.Add(new TranscribedWordInfo
                            {
                                StartTime = ParseOffset((string)annotation["start_offset"]),
                                EndTime = ParseOffset((string)annotation["end_offset"]),
                                Text = (string)annotation["text"],
                                Speaker = (string)annotation["speaker"],
                                StartIndex = annotation["start_index"] == null ? (int?)null : textOffset + (int)annotation["start_index"],
                                EndIndex = annotation["end_index"] == null ? (int?)null : textOffset + (int)annotation["end_index"]
                            });
                        }
                    }
                }

                // 4. The cost (from the tokens of the answer)
                var usage = answer["usage"];
                var inputTokens = (int?)usage?["total_input_tokens"];
                var thoughtTokens = (int?)usage?["total_thought_tokens"] ?? 0;
                var outputTokens = (int?)usage?["total_output_tokens"];
                var byModality = (usage?["input_tokens_by_modality"] as JArray)?
                    .GroupBy(m => ((string)m["modality"] ?? "").ToUpperInvariant())
                    .ToDictionary(g => g.Key, g => g.Sum(m => (int?)m["tokens"] ?? 0));
                var cost = Cost.Create(
                    request.TaskId,
                    this.EngineIdentifier,
                    request.SystemParts.Concat(request.UserParts).ToArray(),
                    watch.Elapsed,
                    request.ItemsIncluded?.Length ?? 1,
                    0,
                    this.EstimatedCostPerInputMillionTokens,
                    this.EstimatedCostPerOutputMillionTokens,
                    inputTokens: inputTokens,
                    outputThoughtsChars: 0,
                    outputThoughtsTokens: outputTokens == null ? (int?)null : thoughtTokens,
                    outputCandidatesChars: text.Length,
                    outputCandidatesTokens: outputTokens == null ? (int?)null : outputTokens - thoughtTokens,
                    byModality);

                var summary = $"RunningTime: {watch.Elapsed}\nInputTokens: {inputTokens}, {GetInputCost(inputTokens ?? 0):C}\nOutputTokens: {outputTokens}, {GetOutputCost(outputTokens ?? 0):C}\nWords: {words.Count}";
                context.CreateVerboseTextFile($"{verbosePrefix}-Resp.txt", text + "\n" + new string('*', 80) + "\n" + summary, request.ProcessStartTime);
                context.WriteInfo($"Transcription received: {words.Count} words, {text.Length} chars ({summary.Replace("\n", ", ")}).");
                PauseIfEnabled(context, this.PauseBeforeSavingResponse, text.ToString());

                return new TranscribeResult { Request = request, Text = text.ToString(), Words = words.ToArray(), Cost = cost, IncompleteReason = incompleteReason };
            }
            finally
            {
                // (the uploaded audio is not kept by Google: deleted, whatever happened)
                if (fileName != null)
                {
                    try { client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, new Uri(baseUri, fileName))).Wait(TimeSpan.FromSeconds(30)); }
                    catch (Exception ex) { context.WriteLog($"Can't delete the uploaded audio '{fileName}': {ex.Message}"); }
                }
            }
        }

        /// <summary>Waits until TranscribeMinTimeBetweenRequests after the last request to this engine (any scene), then reserves its time.</summary>
        private void WaitForTranscribeSlot(SubtitleGeneratorContext context)
        {
            if (this.TranscribeMinTimeBetweenRequests <= TimeSpan.Zero)
                return;
            DateTime slot;
            lock (rs_lastTranscribeRequestByEngine)
            {
                var now = DateTime.UtcNow;
                slot = rs_lastTranscribeRequestByEngine.TryGetValue(this.EngineIdentifier, out var last) && last + this.TranscribeMinTimeBetweenRequests > now
                    ? last + this.TranscribeMinTimeBetweenRequests
                    : now;
                rs_lastTranscribeRequestByEngine[this.EngineIdentifier] = slot;
            }
            var wait = slot - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                WaitStoppable(wait, remaining => context.AIRequestProgressUpdateHandler(ToolName, $"Waiting {remaining.TotalSeconds:0} s (at least {this.TranscribeMinTimeBetweenRequests.TotalSeconds:0} s between two requests)..."));
            }
        }

        private HttpResponseMessage Send(HttpClient client, HttpRequestMessage message)
        {
            var response = client.SendAsync(message).Result;
            AttachToStopWatcher(response);
            if (!response.IsSuccessStatusCode)
            {
                throw AIEngineAPIException.FromHttpStatusCode(response, this.EngineIdentifier);
            }
            return response;
        }

        /// <summary>"2.400s" (a duration of protobuf) => 2.4 s</summary>
        private static TimeSpan ParseOffset(string value)
        {
            if (string.IsNullOrEmpty(value))
                throw new Exception("A word of the transcription has no start / end offset.");
            return TimeSpan.FromSeconds(double.Parse(value.TrimEnd('s'), CultureInfo.InvariantCulture));
        }
    }
}
