using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Progress;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Net.Http;
using System.Threading;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class AIEngineAPI : AIEngine
    {
        [JsonIgnore]
        public abstract string ToolName { get; }

        public bool IsAPIKeyAvailableIfNeeded(SubtitleGeneratorContext context) => (this.APIKeyName == null || context.GetPrivateConfig(this.APIKeyName) != null);

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string BaseAddress { get; set; } = "http://localhost:10000";

        [JsonProperty(Order = 11, Required = Required.Always)]
        public string Model { get; set; }

        [JsonProperty(Order = 12)]
        public bool ValidateModelNameInResponse { get; set; } = false;

        [JsonProperty(Order = 13)]
        public string APIKeyName { get; set; }

        [JsonProperty(Order = 14, TypeNameHandling = TypeNameHandling.None)]
        public ExpandoObject RequestBodyExtension { get; set; }

        [JsonProperty(Order = 15)]
        public TimeSpan TimeOut { get; set; } = TimeSpan.FromMinutes(15);

        [JsonProperty(Order = 16)]
        public double EstimatedCostPerInputMillionTokens { get; set; } = 0.0;

        [JsonProperty(Order = 17)]
        public double EstimatedCostPerOutputMillionTokens { get; set; } = 0.0;

        public double GetInputCost(int nbTokens) => (double)nbTokens / 1_000_000 * EstimatedCostPerInputMillionTokens * CostSaving;
        public double GetOutputCost(int nbTokens) => (double)nbTokens / 1_000_000 * EstimatedCostPerOutputMillionTokens * CostSaving;

        [JsonIgnore]
        protected virtual double CostSaving => 1.0;

        /// <summary>
        /// Set to true when this engine uses the local machine (ex. a local AI server). In --ui mode (several videos
        /// processed in parallel), only one request at a time is then sent to this kind of engine.
        /// </summary>
        [JsonProperty(Order = 19)]
        public bool UseLocalResourceLock { get; set; } = false;

        protected IDisposable AcquireLocalResourceLockIfNeeded(SubtitleGeneratorContext context)
        {
            return this.UseLocalResourceLock
                ? context.UseLocalResource($"AI engine '{EngineIdentifier}'")
                : null; // 'using' accepts null
        }

        [JsonProperty(Order = 20)]
        public bool PauseBeforeSendingRequest { get; set; } = false;
        [JsonProperty(Order = 21)]
        public bool PauseBeforeSavingResponse { get; set; } = false;

        [JsonIgnore]
        public string EngineIdentifierWithoutModel => $"{BaseAddress}|{APIKeyName}";
        [JsonIgnore]
        public virtual string EngineIdentifier => $"{EngineIdentifierWithoutModel}|{Model}";

        protected dynamic CreateRequestBodyWithExtension(AIEngineConfigOverride configOverride)
        {
            dynamic requestBody = new ExpandoObject();
            requestBody = Merge(requestBody, this.RequestBodyExtension);

            if (configOverride?.RequestBodyExtension != null)
            {
                requestBody = Merge(requestBody, configOverride.RequestBodyExtension);
            }

            return requestBody;
        }

        private static dynamic Merge(ExpandoObject item1, ExpandoObject item2)
        {
            if (item2 == null) return item1 ?? new ExpandoObject();
            if (item1 == null) return item2;

            var result = new ExpandoObject();
            var d = result as IDictionary<string, object>;

            foreach (var pair in (IDictionary<string, object>)item1)
            {
                d[pair.Key] = pair.Value;
            }
            foreach (var pair in (IDictionary<string, object>)item2)
            {
                // Recursive merge for nested objects (e.g. generationConfig)
                if (d.TryGetValue(pair.Key, out var existingVal)
                    && existingVal is ExpandoObject expando1
                    && pair.Value is ExpandoObject expando2)
                {
                    d[pair.Key] = Merge(expando1, expando2);
                }
                else
                {
                    d[pair.Key] = pair.Value;
                }
            }

            return result;
        }

        private static Regex rs_timeRegex = new Regex(@"(?<Grab>\""(StartTime|EndTime)\"":\s*\""(?<Time>[^\""]*))\""", RegexOptions.Compiled);

        protected string AddRealTime(SubtitleGeneratorContext context, TimeSpan startOffset, string assistantMessage)
        {
            return rs_timeRegex.Replace(
                assistantMessage,
                match =>
                {
                    try
                    {
                        var grab = match.Groups["Grab"].Value;
                        var requestSpecificTime = TimeSpanExtensions.FlexibleTimeSpanParse(
                            match.Groups["Time"].Value);
                        var globalTime = startOffset + requestSpecificTime;
                        return context.WIP.TimelineMap.IsMultipart
                            ? $"{grab} [{context.WIP.TimelineMap.ConvertToPartSpecificFileIndexAndTime(globalTime)}]\""
                            : $"{grab}\"";
                    }
                    catch (Exception)
                    {
                        return match.Value;
                    }
                });
        }

        /// <summary>'Stop now' on an AI request: shown to the user, to confirm (the engines can change it, ex. a batch still queued: null).</summary>
        internal const string StopNowCostWarning =
            "The request was already sent to the AI: what it already processed may still be billed (the tokens of the request, and what was generated so far), "
            + "and the response is lost.\n\n'Pause after this request' keeps the response (and its cost) and pauses the scene right after ('Resume scene' to continue).";

        /// <summary>The hint of the 'Stop now' button of an AI request.</summary>
        internal const string StopNowHint = "Stops right away: a streaming/normal request is aborted (the response is lost), a batch is cancelled on the server.";

        /// <summary>
        /// Lets the user stop a request that is not a batch (streaming/normal), by clicking 'Stop'
        /// on its task. Aborts the connection (disposing the response is what unblocks a pending read).
        /// Batch requests handle the stop request themselves in their polling loop (they must not be used with this).
        /// </summary>
        protected T RunStoppable<T>(SubtitleGeneratorContext context, HttpClient client, Func<T> work)
        {
            using var watcher = new StopWatcher(context, client);
            try
            {
                var result = work();
                if (watcher.Stopped)
                {
                    throw new UserStoppedWorkerException();
                }
                return result;
            }
            catch (Exception) when (watcher.Stopped)
            {
                // The abort made the pending read/send fail: that's the stop, not an error.
                throw new UserStoppedWorkerException();
            }
        }

        /// <summary>Gives the response to abort if the user asks to stop (see <see cref="RunStoppable"/>).</summary>
        protected static void AttachToStopWatcher(HttpResponseMessage response)
        {
            StopWatcher.Current?.Attach(response);
        }

        private sealed class StopWatcher : IDisposable
        {
            private static readonly AsyncLocal<StopWatcher> rs_current = new AsyncLocal<StopWatcher>();
            public static StopWatcher Current => rs_current.Value;

            private readonly HttpClient r_client;
            private readonly Timer r_timer;
            private HttpResponseMessage m_response;
            private volatile bool m_stopped;

            public bool Stopped => m_stopped;

            public StopWatcher(SubtitleGeneratorContext context, HttpClient client)
            {
                r_client = client;
                rs_current.Value = this;
                // (the timer keeps the current task of this thread, so the poll finds the Stop button of the right task)
                r_timer = new Timer(_ =>
                {
                    if (!m_stopped && context.PollStopRequest())
                    {
                        Stop();
                    }
                }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
            }

            public void Attach(HttpResponseMessage response)
            {
                m_response = response;
                if (m_stopped)
                {
                    TryDispose(response);
                }
            }

            private void Stop()
            {
                m_stopped = true;
                try { r_client.CancelPendingRequests(); } catch { }
                TryDispose(m_response);
            }

            private static void TryDispose(HttpResponseMessage response)
            {
                try { response?.Dispose(); } catch { }
            }

            public void Dispose()
            {
                r_timer.Dispose();
                rs_current.Value = null;
            }
        }

        protected void PauseIfEnabled(SubtitleGeneratorContext context, bool isEnabled, string content = null)
        {
            if (!isEnabled)
                return;

            using (context.EnterUserInteraction("Paused"))
            {
                var answer = context.Output.Ask(new UserPrompt
                {
                    Title = "Paused (PauseBeforeSendingRequest / PauseBeforeSavingResponse)",
                    Message = content,
                    Choices = new[] { "Continue", "Stop this worker" }
                });
                if (answer.Choice != "Continue")
                {
                    throw new UserStoppedWorkerException();
                }
            }
        }

    }
}