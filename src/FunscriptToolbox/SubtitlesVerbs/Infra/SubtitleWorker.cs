using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.Progress;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class SubtitleWorker
    {
        [ConfigDoc("The id of the step: the other steps use what it produces with this id (ex. in Metadatas.Sources), and an override file uses it to change this step.\n" +
            "What it produces is kept in the .wipsubtitles under this id: a new id = done again from the start.")]
        [JsonProperty(Order = 1, Required = Required.Always)]
        public string Id { get; set; }

        [ConfigDoc("false: the step is skipped (and the steps that need what it produces wait). The main configuration defines the steps disabled; each workflow override enables the ones it uses.")]
        [JsonProperty(Order = 2)]
        public bool Enabled { get; set; } = false;

        [ConfigDoc("What the step produces, as shown in the web UI and the logs: '<Produces> '<Id>'' (ex. \"Translation\", \"Analysis & Translation\"). null: the name of its kind of step (ex. \"Transcription\").")]
        [JsonProperty(Order = 3)]
        public string Produces { get; set; }

        [ConfigDoc("What the step does, in the logs: '<Verb> '<Id>'...' (ex. \"Translating\"). null: the verb of its kind of step (ex. \"Transcribing\").")]
        [JsonProperty(Order = 4)]
        public string Verb { get; set; }

        [ConfigDoc("true: the step runs again each time the scene is processed, even when it's already done (ex. an output file written again with the latest data).",
            Advice = "Leave false. Set it to true temporarily to see the effect of a change on a step that is already done (or use 'Redo this step' in the Flow view).")]
        [JsonProperty(Order = 5)]
        public bool CanBeUpdated { get; set; } = false;

        protected string GetId() => this.Id;

        /// <summary>The name of the kind of step (when Produces is not set), ex. "Transcription".</summary>
        protected abstract string GetDefaultProduces();

        /// <summary>The verb of the kind of step (when Verb is not set), ex. "Transcribing".</summary>
        protected abstract string GetDefaultVerb();

        private string GetProducesLabel() => string.IsNullOrWhiteSpace(this.Produces) ? GetDefaultProduces() : this.Produces;
        private string GetVerbLabel() => string.IsNullOrWhiteSpace(this.Verb) ? GetDefaultVerb() : this.Verb;

        /// <summary>False: the worker is not run and has no task (ex. an alias, only a name resolved when it's used).</summary>
        internal virtual bool IsTask => true;

        /// <summary>How the worker is shown in the task tree of the web UI.</summary>
        internal string DisplayName => $"{GetProducesLabel()} '{GetId()}'";

        /// <summary>What the worker uses and produces (the 'Flow' view of the web UI).</summary>
        internal WorkerDataFlow GetDataFlow()
        {
            var flow = new WorkerDataFlow();
            DescribeDataFlow(flow);
            return flow;
        }

        /// <summary>
        /// Adds what the worker uses (flow.Input...) and produces (flow.Output...). Only used to show the 'Flow' view:
        /// it must follow what the worker really reads / writes (overrides call the base first).
        /// </summary>
        protected virtual void DescribeDataFlow(WorkerDataFlow flow)
        {
        }

        public void Execute(SubtitleGeneratorContext context)
        {
            // --- Pre-Execution Guards ---
            if (!this.Enabled || !this.IsTask)
            {
                return;
            }

            using var node = context.BeginWorkerTask(this, DisplayName);

            // (in the page, the start / end of the worker are shown from its task: "Started: ...", "Finished: ... (duration)",
            //  "Skipped: ...: already done", "Blocked: ...: reason". The same messages here only go in the log file)
            if (!IsPrerequisitesMet(context, out var reasonPrerequisites))
            {
                context.WriteLog($"{GetProducesLabel()} '{GetId()}' can't be done yet: {reasonPrerequisites}");
                node.SetState(TaskState.Blocked, reasonPrerequisites);
                context.AddBlockedWorker(DisplayName);
                return;
            }

            // --- Core Execution Logic ---
            // Already finished: runs again only when something changed since the last run, or when the worker can be updated.
            // In the second case, it's only a refresh (the UI doesn't count the scene as changed).
            string reasonNeedsToRun = null;
            var wasFinished = IsFinished(context);
            var needsToRun = !wasFinished;
            if (wasFinished)
            {
                if (HasChangedSinceLastRun(context, out reasonNeedsToRun))
                {
                    needsToRun = true;
                }
                else if (this.CanBeUpdated)
                {
                    needsToRun = true;
                    reasonNeedsToRun = "worker is updatable";
                    node.MarkAsRefreshOnly();
                }
            }

            if (needsToRun)
            {
                var added = reasonNeedsToRun != null ? $" ({reasonNeedsToRun})" : string.Empty;
                context.WriteLog($"{GetVerbLabel()} '{GetId()}'...{added}");
                if (reasonNeedsToRun != null)
                    context.WriteInfo($"Done again: {reasonNeedsToRun}.");
                var watch = Stopwatch.StartNew();
                try
                {
                    EnsureDataObjectExists(context);
                    DoWork(context);
                    watch.Stop();

                    if (IsFinished(context))
                        context.WriteLog($"Finished in {watch.Elapsed}:");
                    else
                        context.WriteInfo($"Not finished yet in {watch.Elapsed}.");
                    foreach (var line in GetAdditionalStatusLines(context))
                    {
                        context.WriteInfo(line);
                    }
                }
                catch (UserStoppedWorkerException ex)
                {
                    // (not an error: the user asked for it, like a pause of the scene)
                    node.SetState(TaskState.Cancelled, "stopped by the user");
                    context.WriteInfo($"{GetProducesLabel()} '{GetId()}' stopped by the user: what was already received is kept, it continues at the next run (Retry or Rescan).", ConsoleColor.Yellow);
                    context.WriteLog(ex.ToString());
                }
                catch (Exception ex)
                {
                    node.Fail(null);
                    context.WriteError($"An error occured while {GetVerbLabel().ToLower()} '{GetId()}':\n{ex.Message}");
                    context.WriteLog(ex.ToString());
                }
                finally
                {
                    AfterWork(context, wasAlreadyFinished: false);
                }
            }
            else
            {
                context.WriteLog($"{GetProducesLabel()} '{GetId()}' has already been done.");
                foreach (var line in GetAdditionalStatusLines(context))
                {
                    context.WriteInfoAlreadyDone(line);
                }
                AfterWork(context, wasAlreadyFinished: true);
                node.SetState(TaskState.Skipped, "already done");
            }
        }


        protected abstract bool IsPrerequisitesMet(SubtitleGeneratorContext context, out string reason);
        protected abstract bool IsFinished(SubtitleGeneratorContext context);
        protected abstract void EnsureDataObjectExists(SubtitleGeneratorContext context);
        protected abstract void DoWork(SubtitleGeneratorContext context);
        protected virtual void AfterWork(SubtitleGeneratorContext context, bool wasAlreadyFinished) 
        { 
        }
        /// <summary>
        /// For a worker already finished: true when something it depends on changed since its last run (it must run again).
        /// (a worker with 'CanBeUpdated' also runs again, but only as a refresh: see Execute)
        /// </summary>
        protected virtual bool HasChangedSinceLastRun(SubtitleGeneratorContext context, out string reason)
        {
            reason = null;
            return false;
        }
        protected virtual IEnumerable<string> GetAdditionalStatusLines(SubtitleGeneratorContext context) 
        { 
            yield break; 
        }

        protected void DoExportMetatadaSrt(
            SubtitleGeneratorContext context,
            TimedItemWithMetadataCollection container,
            bool wasAlreadyFinished)
        {
            var extension = $".Worker.{container.Id}.srt";
            if (!wasAlreadyFinished || !context.WIP.TimelineMap.GetFullPaths(context.WIP.ParentPath)
                .Any(fullpath => File.Exists(Path.ChangeExtension(fullpath, extension))))
            {
                var virtualSubtitleFile = context.WIP.CreateVirtualSubtitleFile();
                virtualSubtitleFile.Subtitles.AddRange(CreateMetadataSubtitles(container));
                virtualSubtitleFile.Save(
                context.WIP.ParentPath,
                extension,
                context.SoftDelete);

                var fullfilePath = context.WIP.BaseFilePath + extension;
                if (context.WIP.TimelineMap.IsMultipart && (!wasAlreadyFinished || File.Exists(fullfilePath)))
                {
                    var newFullFile = new SubtitleFile();
                    newFullFile.Subtitles.AddRange(
                        virtualSubtitleFile.Subtitles.Select(item =>
                            new Subtitle(item.StartTime, item.EndTime, item.Text)));
                    newFullFile.SaveSrt(fullfilePath);
                }
            }
        }

        protected static IEnumerable<Subtitle> CreateMetadataSubtitles(TimedItemWithMetadataCollection container)
        {
            return container.GetItems().Select(item =>
                new Subtitle(
                    item.StartTime,
                    item.EndTime,
                    string.Join("\n", item.Metadata.Select(kvp => $"{{{kvp.Key}:{AddNewLineIfMultilines(kvp.Value)}}}"))));
        }

        protected static IEnumerable<TimedItemWithMetadata> ReadMetadataSubtitles(IEnumerable<Subtitle> subtitles)
        {
            const string MetadataExtractionRegex = @"{(?<name>[^}:]*)(\:(?<value>[^}]*))?}";
            foreach (var subtitle in subtitles)
            {
                var metadatas = new MetadataCollection();
                foreach (var match in Regex
                        .Matches(subtitle.Text, MetadataExtractionRegex)
                        .Cast<Match>())
                {
                    metadatas[match.Groups["name"].Value] = match.Groups["value"].Success ? match.Groups["value"].Value : string.Empty;
                }
                yield return new TimedItemWithMetadata(
                    subtitle.StartTime,
                    subtitle.EndTime,
                    metadata: metadatas);
            }
        }

        protected static string[] GetPrivateMetadataNamesArray(string privateMetadataNames)
        {
            return (privateMetadataNames == null)
                ? Array.Empty<string>()
                : privateMetadataNames.Split(',').Select(f => f.Trim()).ToArray();
        }

        protected static string AddNewLineIfMultilines(string value)
        {
            return value == null
                ? null
                : (value.Contains("\n")) 
                    ? "\n" + value
                    : value;
        }
    }
}