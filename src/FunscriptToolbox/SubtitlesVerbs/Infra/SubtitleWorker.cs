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
        [JsonProperty(Order = 2)] // Order = 1 is the Id
        public bool Enabled { get; set; } = false;

        [JsonProperty(Order = 5)]
        public bool CanBeUpdated { get; set; } = false;

        protected abstract string GetId();
        protected abstract string GetWorkerTypeName();
        protected abstract string GetExecutionVerb();

        /// <summary>How the worker is shown in the task tree of the web UI.</summary>
        internal string DisplayName => $"{GetWorkerTypeName()} '{GetId()}'";

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
            if (!this.Enabled)
            {
                return;
            }

            using var node = context.BeginWorkerTask(this, DisplayName);

            if (!IsPrerequisitesMet(context, out var reasonPrerequisites))
            {
                context.WriteInfo($"{GetWorkerTypeName()} '{GetId()}' can't be done yet: {reasonPrerequisites}");
                context.WriteInfo();
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
                context.WriteInfo($"{GetExecutionVerb()} '{GetId()}'...{added}");
                var watch = Stopwatch.StartNew();
                try
                {
                    EnsureDataObjectExists(context);
                    DoWork(context);
                    watch.Stop();

                    context.WriteInfo(IsFinished(context)
                        ? $"Finished in {watch.Elapsed}:"
                        : $"Not finished yet in {watch.Elapsed}.");
                    foreach (var line in GetAdditionalStatusLines(context))
                    {
                        context.WriteInfo($"    {line}");
                    }
                    context.WriteInfo();
                }
                catch (UserStoppedWorkerException ex)
                {
                    node.SetState(TaskState.Cancelled, "stopped by the user");
                    context.WriteError($"An error occured while {GetExecutionVerb().ToLower()} '{GetId()}':\n{ex.Message}");
                    context.WriteLog(ex.ToString());
                }
                catch (Exception ex)
                {
                    node.Fail(null);
                    context.WriteError($"An error occured while {GetExecutionVerb().ToLower()} '{GetId()}':\n{ex.Message}");
                    context.WriteLog(ex.ToString());
                }
                finally
                {
                    AfterWork(context, wasAlreadyFinished: false);
                    context.WriteInfo();
                }
            }
            else
            {
                context.WriteInfoAlreadyDone($"{GetWorkerTypeName()} '{GetId()}' has already been done.");
                foreach (var line in GetAdditionalStatusLines(context))
                {
                    context.WriteInfoAlreadyDone($"    {line}");
                }
                context.WriteInfoAlreadyDone();
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