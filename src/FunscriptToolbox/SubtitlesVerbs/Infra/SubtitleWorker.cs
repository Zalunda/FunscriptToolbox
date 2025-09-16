using FunscriptToolbox.Core;
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

        public void Execute(SubtitleGeneratorContext context)
        {
            // --- Pre-Execution Guards ---
            if (!this.Enabled)
            {
                return;
            }

            if (!IsPrerequisitesMet(context, out var reason))
            {
                context.WriteInfo($"{GetWorkerTypeName()} '{GetId()}' can't be done yet: {reason}");
                context.WriteInfo();
                return;
            }

            // --- Core Execution Logic ---
            if (!IsFinished(context) || NeedsToRun(context, out reason))
            {
                var added = reason != null ? $" ({reason})" : string.Empty;
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
                catch (Exception ex)
                {
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
            }
        }


        protected abstract bool IsPrerequisitesMet(SubtitleGeneratorContext context, out string reason);
        protected abstract bool IsFinished(SubtitleGeneratorContext context);
        protected abstract void EnsureDataObjectExists(SubtitleGeneratorContext context);
        protected abstract void DoWork(SubtitleGeneratorContext context);
        protected virtual void AfterWork(SubtitleGeneratorContext context, bool wasAlreadyFinished) 
        { 
        }
        protected virtual bool NeedsToRun(SubtitleGeneratorContext context, out string reason)
        {
            if (this.CanBeUpdated)
            {
                reason = "worker is updatable";
                return true;
            }
            reason = "worker is not updatable";
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
            if (!wasAlreadyFinished || !context.WIP.TimelineMap.GetFullPaths(context.WIP.ParentPath).Any(fullpath => File.Exists(fullpath)))
            {
                var virtualSubtitleFile = context.WIP.CreateVirtualSubtitleFile();
                virtualSubtitleFile.Subtitles.AddRange(CreateMetadataSubtitles(container));
                virtualSubtitleFile.Save(
                context.WIP.ParentPath,
                $".Worker.{container.Id}.srt",
                context.SoftDelete);
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

            return subtitles
                .Select(subtitle => new TimedItemWithMetadata(
                    subtitle.StartTime,
                    subtitle.EndTime,
                    metadata: new MetadataCollection(
                        Regex
                        .Matches(subtitle.Text, MetadataExtractionRegex)
                        .Cast<Match>()
                        .ToDictionary(
                            match => match.Groups["name"].Value,
                            match => match.Groups["value"].Success ? match.Groups["value"].Value : string.Empty))));
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