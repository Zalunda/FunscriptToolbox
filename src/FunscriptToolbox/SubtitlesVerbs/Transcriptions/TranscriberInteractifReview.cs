using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Progress;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    /// <summary>
    /// The review of the file the user revises (ex. '.manual-input.srt'), in the 'Timing' editor of the web UI, instead of editing the file by hand.
    /// It replaces the output that creates that file (ex. SubtitleOutputSrt 'generated-manual-input-srt'); the file is still imported by the next worker
    /// (ex. TranscriberImportMetadatas 'manual-input'), so the file can still be edited by hand once the review is done
    /// (its metadata, ex. {Note:...}, are always used; its timings only until the next steps use them, then they are frozen).
    /// - First run: the subtitles to review are created in the .wipsubtitles (job state): from the file when it already exists (imported once,
    ///   with what was validated in the editor: ex. a review started with the file), otherwise from Metadatas / TextSources, like SubtitleOutputSrt.
    /// - Then the worker is blocked until the user clicks 'Review done' in the editor (what the user does is saved in the job state).
    /// - Then the file is written (without the line that blocks its import, ex. '[USER-REVISION-NEEDED]'), and the transcription is finished.
    /// </summary>
    public class TranscriberInteractifReview : Transcriber
    {
        /// <summary>A subtitle of the review: its timing, its lines as in the file (text and {Name:Value} metadata), validated or not.</summary>
        public class ReviewItem
        {
            public TimeSpan StartTime { get; set; }
            public TimeSpan EndTime { get; set; }
            public string[] Lines { get; set; }
            public bool Validated { get; set; }
        }

        public class JobState
        {
            /// <summary>Where the subtitles to review came from (shown in the editor).</summary>
            public string Origin { get; set; }
            public List<ReviewItem> Items { get; set; } = new List<ReviewItem>();
            /// <summary>The user clicked 'Review done': the file is written (by the next run of the worker).</summary>
            public bool IsDone { get; set; }
            /// <summary>Incremented at each save of the subtitles (a page that saves an older version is refused).</summary>
            public int Version { get; set; }
            /// <summary>Imported from a file that was already reviewed (the old way): the file is not written again.</summary>
            public bool FileAlreadyReviewed { get; set; }
            /// <summary>
            /// How long the user worked on the review in the Timing editor: only while the page is used
            /// (visible, with the focus, and the user did something in the last minute, or it's playing).
            /// </summary>
            public TimeSpan ActiveTime { get; set; }
        }

        private const string ToolName = "InteractifReview";

        [JsonProperty(Order = 20, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [JsonProperty(Order = 21)]
        public MetadataAggregator Metadatas { get; set; }
        [JsonProperty(Order = 22)]
        public string TextSources { get; set; }
        [JsonProperty(Order = 23)]
        public string MetadataToUse { get; set; }
        [JsonProperty(Order = 24)]
        public string AddToFirstSubtitle { get; set; }
        /// <summary>False: no review, the file is written right away (ex. 'fully automatic').</summary>
        [JsonProperty(Order = 25)]
        public bool ReviewNeeded { get; set; } = true;
        /// <summary>True: when the file already exists the first time, the review starts from it (ex. a review started by editing the file).</summary>
        [JsonProperty(Order = 26)]
        public bool ImportExistingFile { get; set; } = true;

        protected override string GetMetadataProduced() => null;
        protected override string GetExecutionVerb() => "Reviewing";

        protected override void DescribeDataFlow(WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            flow.InputMetadatas(this.Metadatas);
            flow.InputSources(this.TextSources, "text");
            flow.Output(DataKind.File, this.FileSuffix);
        }

        internal JobState GetJobState(WorkInProgressSubtitles wip)
            => wip.Transcriptions.FirstOrDefault(t => t.Id == this.TranscriptionId)?.CurrentJobState as JobState;

        /// <summary>What the web UI offers to the user while the review is not done (see ITaskNode.SetUserAction).</summary>
        private string UserAction => $"timing:{this.TranscriptionId}";

        private string WaitingReason => $"waiting for the review of '{this.FileSuffix}' in the Timing editor of the web UI (button 'Timing' of the scene), then 'Review done'";

        protected override bool IsPrerequisitesMet(SubtitleGeneratorContext context, out string reason)
        {
            var jobState = GetJobState(context.WIP);
            if (jobState?.IsDone == true)
            {
                // (only the file to write: possible without the web UI)
                reason = null;
                return true;
            }
            if (!context.Output.IsWebUi && this.ReviewNeeded)
            {
                reason = "Not supported in console mode: the review is done in the Timing editor of the web UI (--ui).";
                return false;
            }
            if (jobState != null)
            {
                reason = this.WaitingReason;
                context.AddUserTodo($"Review '{this.FileSuffix}' in the Timing editor, then click 'Review done'.");
                // (the scene is shown as 'needs you' in the web UI, with a button to open the review)
                TaskScope.Current.SetUserAction(this.UserAction);
                return false;
            }

            // Not started yet: what the subtitles to review are created from
            if (this.ImportExistingFile && context.WIP.LoadVirtualSubtitleFile(this.FileSuffix) != null)
            {
                reason = null;
                return true;
            }
            if (this.Metadatas == null)
            {
                reason = "'Metadatas' must be set (the timings and metadata of the subtitles to review).";
                return false;
            }
            return this.Metadatas.Aggregate(context).IsPrerequisitesMetOnlyTimings(out reason);
        }

        protected override bool IsFinished(SubtitleGeneratorContext context)
        {
            // (the file deleted after the review: written again)
            return base.IsFinished(context) && context.WIP.LoadVirtualSubtitleFile(this.FileSuffix) != null;
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Transcription transcription)
        {
            var jobState = transcription.CurrentJobState as JobState;
            if (jobState == null)
            {
                jobState = CreateJobState(context);
                if (!this.ReviewNeeded)
                    jobState.IsDone = true;
                context.WIP.ExecuteLocked(() => transcription.CurrentJobState = jobState);
                context.WIP.Save();
                context.WriteInfo($"  {jobState.Items.Count} subtitles to review ({jobState.Origin}).");

                if (!jobState.IsDone)
                {
                    // (the scene stops here: the user reviews in the web UI, then 'Review done' rescans the scene)
                    TaskScope.Current.SetUserAction(this.UserAction);
                    TaskScope.Current.SetState(TaskState.Blocked, this.WaitingReason);
                    context.AddBlockedWorker(DisplayName);
                    context.AddUserTodo($"Review '{this.FileSuffix}' in the Timing editor, then click 'Review done'.");
                    return;
                }
            }

            WriteFileAndFinish(context, transcription, jobState);
        }

        // ---- The subtitles to review ----

        private JobState CreateJobState(SubtitleGeneratorContext context)
        {
            var existingFile = this.ImportExistingFile ? context.WIP.LoadVirtualSubtitleFile(this.FileSuffix) : null;
            if (existingFile != null)
            {
                // Already reviewed the old way (the line that blocks its import was removed, or it was already imported): nothing to review
                var markers = GetImportMarkers(context);
                var importerIds = GetImporters(context).Select(w => w.TranscriptionId).ToArray();
                var alreadyReviewed = (markers.Length > 0 && !existingFile.Subtitles.Any(s => s.Lines.Any(l => markers.Any(m => l.Contains(m)))))
                    || context.WIP.Transcriptions.Any(t => importerIds.Contains(t.Id) && t.IsFinished);

                // (what was validated in the editor when it was editing the file directly)
                var validated = ReadValidatedFromOldEditor(context.WIP);
                return new JobState
                {
                    IsDone = alreadyReviewed,
                    FileAlreadyReviewed = alreadyReviewed,
                    Origin = $"imported from the file '{this.FileSuffix}'"
                        + (alreadyReviewed ? " (already reviewed)" : string.Empty)
                        + (validated.Count > 0 ? $", {validated.Count} validated" : string.Empty),
                    Items = existingFile.Subtitles.Select(s => new ReviewItem
                    {
                        StartTime = s.StartTime,
                        EndTime = s.EndTime,
                        Lines = SplitLines(s.Lines),
                        Validated = validated.Contains($"{(long)s.StartTime.TotalMilliseconds}-{(long)s.EndTime.TotalMilliseconds}")
                    }).ToList()
                };
            }

            // Like SubtitleOutputSrt (with UseBySourceMetadatas = false): the text of TextSources, then the metadata of the timing
            var aggregation = this.Metadatas.Aggregate(context);
            var timings = aggregation.ReferenceTimingsWithMetadata;
            var textSourceIds = this.TextSources?.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0).ToArray() ?? Array.Empty<string>();
            var (textSourcesRaw, _) = this.Metadatas.GetOthersSources(context, textSourceIds);
            var textSources = textSourcesRaw.Select(osr => osr.GetAnalysis(timings)).ToArray();

            var items = new List<ReviewItem>();
            foreach (var timing in timings)
            {
                var lines = new List<string>();
                foreach (var textSource in textSources)
                {
                    if (!textSource.TimingsWithOverlapItems.TryGetValue(timing, out var overlaps))
                        continue;
                    foreach (var overlap in overlaps)
                    {
                        var text = overlap.Item.Metadata.Get(this.MetadataToUse ?? textSource.Container.MetadataAlwaysProduced);
                        if (text == null) continue;
                        lines.AddRange(SplitLines(new[] { textSources.Length > 1 ? $"[{textSource.Container.Id}] {text}" : text }));
                    }
                }
                foreach (var metadata in timing.Metadata)
                {
                    lines.AddRange(SplitLines(new[] { $"{{{metadata.Key}:{metadata.Value}}}" }));
                }
                items.Add(new ReviewItem { StartTime = timing.StartTime, EndTime = timing.EndTime, Lines = lines.ToArray() });
            }

            if (!string.IsNullOrEmpty(this.AddToFirstSubtitle))
            {
                var added = SplitLines(new[] { this.AddToFirstSubtitle });
                if (items.Count > 0)
                    items[0].Lines = items[0].Lines.Concat(added).ToArray();
                else
                    items.Add(new ReviewItem { StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromSeconds(5), Lines = added });
            }

            return new JobState
            {
                Origin = $"created from '{this.Metadatas.TimingsSource}'",
                Items = items
            };
        }

        /// <summary>The subtitles validated in the Timing editor when it edited the file directly ('&lt;scene&gt;.manual-input.timing-validated.json').</summary>
        private HashSet<string> ReadValidatedFromOldEditor(WorkInProgressSubtitles wip)
        {
            var path = wip.BaseFilePath + Path.GetFileNameWithoutExtension(this.FileSuffix) + ".timing-validated.json";
            if (!File.Exists(path))
                return new HashSet<string>();
            var content = JsonConvert.DeserializeAnonymousType(File.ReadAllText(path), new { Validated = Array.Empty<string>() });
            return new HashSet<string>(content?.Validated ?? Array.Empty<string>());
        }

        /// <summary>The lines as they are in the .srt file (a line with line breaks is several lines).</summary>
        internal static string[] SplitLines(IEnumerable<string> lines)
            => (lines ?? Array.Empty<string>())
                .SelectMany(l => (l ?? string.Empty).Replace("\r\n", "\n").Split('\n', '\r'))
                .ToArray();

        // ---- Review done ----

        /// <summary>The workers that import the file (ex. TranscriberImportMetadatas 'manual-input').</summary>
        private TranscriberImportMetadatas[] GetImporters(SubtitleGeneratorContext context)
            => context.Config.Workers
                .OfType<TranscriberImportMetadatas>()
                .Where(w => w.Enabled && string.Equals(w.FileSuffix, this.FileSuffix, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        /// <summary>The line that blocks the import of the file until the user reviewed it (ex. '[USER-REVISION-NEEDED]').</summary>
        private string[] GetImportMarkers(SubtitleGeneratorContext context)
            => GetImporters(context)
                .Where(w => !string.IsNullOrEmpty(w.ProcessOnlyWhenStringIsRemoved))
                .Select(w => w.ProcessOnlyWhenStringIsRemoved)
                .ToArray();

        private void WriteFileAndFinish(SubtitleGeneratorContext context, Transcription transcription, JobState jobState)
        {
            // (the review is done: without the line that blocks the import of the file)
            var markers = GetImportMarkers(context);

            ReviewItem[] items = null;
            context.WIP.ExecuteLocked(() => items = jobState.Items.OrderBy(i => i.StartTime).ToArray());

            // (how long the review took: in the first subtitle, as a text line, not a metadata)
            var activeTime = jobState.ActiveTime;
            var reviewTimeLine = $"(Timing review: {(int)activeTime.TotalHours}:{activeTime:mm\\:ss} of active work in the Timing editor, {items.Length} subtitles, {items.Count(i => i.Validated)} validated)";

            var file = context.WIP.CreateVirtualSubtitleFile();
            foreach (var item in items)
            {
                var lines = (item.Lines ?? Array.Empty<string>()).Where(l => !markers.Any(m => l.Contains(m)));
                if (file.Subtitles.Count == 0 && activeTime > TimeSpan.Zero)
                    lines = lines.Concat(new[] { reviewTimeLine });
                file.Subtitles.Add(new Subtitle(item.StartTime, item.EndTime, lines.ToArray()));
            }
            if (activeTime > TimeSpan.Zero)
            {
                context.WriteInfo($"  Review of '{this.FileSuffix}': {(int)activeTime.TotalHours}:{activeTime:mm\\:ss} of active work in the Timing editor.");
            }
            if (jobState.FileAlreadyReviewed && context.WIP.LoadVirtualSubtitleFile(this.FileSuffix) != null)
            {
                // (reviewed the old way: the file stays as it is)
                context.WriteInfo($"  '{this.FileSuffix}' was already reviewed: kept as it is.");
            }
            else
            {
                file.Save(context.WIP.ParentPath, this.FileSuffix, context.SoftDelete);
                context.WriteInfo($"  '{this.FileSuffix}' written ({items.Length} subtitles, {items.Count(i => i.Validated)} validated).");
            }

            context.WIP.ExecuteLocked(() =>
            {
                transcription.Items.Clear();
                transcription.Items.AddRange(
                    ReadMetadataSubtitles(file.Subtitles)
                    .Select(f => new TranscribedItem(f.StartTime, f.EndTime, f.Metadata)));
                // (the time of the user, in the costs: like the other interactive workers)
                if (activeTime > TimeSpan.Zero && !transcription.Costs.Any(c => c.EngineIdentifier == ToolName))
                    transcription.Costs.Add(new Cost(transcription.Id, ToolName, activeTime, items.Length));
                transcription.MarkAsFinished();
            });
            context.WIP.Save();
        }
    }
}
