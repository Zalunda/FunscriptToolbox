using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Inputs;
using FunscriptToolbox.SubtitlesVerbs.Progress;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Processors
{
    /// <summary>
    /// The review of the file the user revises (ex. '.manual-input.srt'), in the 'Timing' editor of the web UI, instead of editing the file by hand.
    /// It replaces the output that creates that file (ex. OutputSrt 'generated-manual-input-srt'); the file is still imported by the next worker
    /// (ex. InputImportMetadatas 'manual-input'), so the file can still be edited by hand once the review is done
    /// (its metadata, ex. {Note:...}, are always used; its timings only until the next steps use them, then they are frozen).
    /// - First run: the subtitles to review are created in the .wipsubtitles (job state): from the file when it already exists (imported once,
    ///   with what was validated in the editor: ex. a review started with the file), otherwise from Metadatas / TextSources, like OutputSrt.
    /// - Then the worker is blocked until the user clicks 'Review done' in the editor (what the user does is saved in the job state).
    /// - Then the file is written (without the line that blocks its import, ex. '[USER-REVISION-NEEDED]'), and the transcription is finished.
    /// </summary>
    [ConfigDoc("The review of the file you revise (ex. '.manual-input.srt') in the Review editor of the web UI, instead of editing the file by hand: passes 'Timing', 'Review warnings' and 'Speakers'. " +
        "When you click 'Review done', the file is written (then imported by the next step).")]
    public class TransformReview : Transform
    {
        /// <summary>
        /// A subtitle of the review: its timing, its lines as in the file (text, {Name:Value} metadata, '[REVIEW:...]' / '[CHECKED:...]' review lines),
        /// its timing validated or not. (Whether its speaker is known is not kept: it comes from its metadata, see the Timing editor.)
        /// </summary>
        public class ReviewItem
        {
            public TimeSpan StartTime { get; set; }
            public TimeSpan EndTime { get; set; }
            public string[] Lines { get; set; }
            /// <summary>Its timing (the whole subtitle: its start and its end) is finalized by the user, see the Review editor.</summary>
            public bool Finalized { get; set; }
            /// <summary>
            /// The user chose / confirmed its speaker in the Review editor: a speaker to confirm (ex. {Speaker:Multiple}, from the AI) is to do
            /// until then. Only in the review: nothing in the file.
            /// </summary>
            public bool SpeakerConfirmed { get; set; }

            /// <summary>Read from the reviews saved before the timing was finalized as a whole.</summary>
            [JsonProperty("TimingValidated")]
            private bool LegacyTimingValidated { set { if (value) this.Finalized = true; } }

            /// <summary>Read from the reviews saved when the start / the end were finalized on their own: finalized = both were.</summary>
            [JsonProperty("StartFinalized")]
            private bool LegacyStartFinalized { set { m_legacyStart = value; this.Finalized = m_legacyStart && m_legacyEnd; } }
            [JsonProperty("EndFinalized")]
            private bool LegacyEndFinalized { set { m_legacyEnd = value; this.Finalized = m_legacyStart && m_legacyEnd; } }
            private bool m_legacyStart;
            private bool m_legacyEnd;
        }

        /// <summary>A speaker of the list of the Timing editor: its name (in the metadata), an alias (shorter, only shown in the editor)
        /// and an image (a part of a frame of the video, chosen by the user, shown in the editor: a data URL, ex. "data:image/jpeg;base64,...").</summary>
        [JsonConverter(typeof(ReviewSpeakerConverter))]
        public class ReviewSpeaker
        {
            public string Name { get; set; }
            public string Alias { get; set; }
            public string Image { get; set; }
        }

        /// <summary>A speaker saved as a name only (before the aliases): read as a speaker without alias.</summary>
        private class ReviewSpeakerConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType) => objectType == typeof(ReviewSpeaker);
            public override bool CanWrite => false;

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                var token = Newtonsoft.Json.Linq.JToken.Load(reader);
                if (token.Type == Newtonsoft.Json.Linq.JTokenType.String)
                    return new ReviewSpeaker { Name = (string)token };
                if (token.Type != Newtonsoft.Json.Linq.JTokenType.Object)
                    return null;
                return new ReviewSpeaker { Name = (string)token["Name"], Alias = (string)token["Alias"], Image = (string)token["Image"] };
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) => throw new NotSupportedException();
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
            /// <summary>The speakers of the scene, as listed in the Timing editor, in its order (also the ones not used yet). Saved with the subtitles.</summary>
            public List<ReviewSpeaker> Speakers { get; set; } = new List<ReviewSpeaker>();
            /// <summary>Imported from a file that was already reviewed (the old way): the file is not written again.</summary>
            public bool FileAlreadyReviewed { get; set; }
            /// <summary>
            /// How long the user worked on the review in the Timing editor: only while the page is used
            /// (visible, with the focus, and the user did something in the last minute, or it's playing).
            /// </summary>
            public TimeSpan ActiveTime { get; set; }
            /// <summary>
            /// The parts of the scene that the user listened to, to validate the gaps (the parts without subtitle, see the Timing editor: a gap is
            /// validated when it's inside one of them). Sorted, not overlapping. Kept as parts of the scene, not by gap: a gap that grows
            /// (ex. a subtitle shortened) is to do again only for its new part.
            /// </summary>
            public List<ReviewTimeRange> ValidatedGaps { get; set; } = new List<ReviewTimeRange>();
        }

        /// <summary>A part of the scene (in the time of the whole scene).</summary>
        public class ReviewTimeRange
        {
            public TimeSpan Start { get; set; }
            public TimeSpan End { get; set; }
        }

        private const string ToolName = "InteractifReview";

        [ConfigDoc("The file written when the review is done (ex. \".manual-input.srt\"), then imported by the next step (ex. 'manual-input').")]
        [JsonProperty(Order = 20, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [ConfigDoc("The subtitles to review (TimingsSource) and the steps whose metadata are added to them (Sources).")]
        [JsonProperty(Order = 21)]
        public MetadataAggregator Metadatas { get; set; }
        [ConfigDoc("The ids of the steps whose text is put in the subtitles to review, separated by ',' (ex. \"manual-input-preparer\"; add a translation, ex. \"full-ai_google\", to also have a basic translation).")]
        [JsonProperty(Order = 22)]
        public string TextSources { get; set; }
        [ConfigDoc("The metadata used as the text of the sources (null: the main metadata of each source).")]
        [JsonProperty(Order = 23)]
        public string MetadataToUse { get; set; }
        [ConfigDoc("Added to the first subtitle, for you to fill (ex. \"{OngoingContext:...}\", \"{OngoingSpeakers:...}\": the context of the scene, the speakers). " +
            "The other properties (ex. GrabOnScreenText, VisualTraining) are added in the Properties tab of the Review editor.")]
        [JsonProperty(Order = 24)]
        public string AddToFirstSubtitle { get; set; }
        [ConfigDoc("false: no review, the file is written right away ('fully automatic'). true: the scene waits until you click 'Review done' in the Review editor (button 'Review' of the scene).")]
        [JsonProperty(Order = 25)]
        public bool ReviewNeeded { get; set; } = true;
        [ConfigDoc("When the file already exists the first time, the review starts from it (ex. a review started by editing the file by hand).")]
        [JsonProperty(Order = 26)]
        public bool ImportExistingFile { get; set; } = true;
        [ConfigDoc("The metadata of the speaker of a subtitle (ex. \"Speaker\"), set in the 'Speakers' pass of the Review editor. null (with OngoingSpeakersMetadata): no 'Speakers' pass.")]
        [JsonProperty(Order = 27)]
        public string SpeakerMetadata { get; set; }
        [ConfigDoc("The metadata of the speakers present from a subtitle on (ex. \"OngoingSpeakers\": \"A, B\"). It must start with \"Ongoing\" (carried to the next subtitles). " +
            "A subtitle's speaker is known when it has SpeakerMetadata, or when only one speaker is present; otherwise, the AI decides.")]
        [JsonProperty(Order = 28)]
        public string OngoingSpeakersMetadata { get; set; }

        /// <summary>The 'Speakers' pass of the Timing editor is possible (both metadata are configured, see <see cref="GetSpeakersConfigError"/>).</summary>
        internal bool HasSpeakersPass => this.SpeakerMetadata != null && this.OngoingSpeakersMetadata != null;

        /// <summary>A problem with SpeakerMetadata / OngoingSpeakersMetadata, or null.</summary>
        internal string GetSpeakersConfigError()
        {
            if ((this.SpeakerMetadata == null) != (this.OngoingSpeakersMetadata == null))
                return "'SpeakerMetadata' and 'OngoingSpeakersMetadata' must both be set (ex. \"Speaker\" and \"OngoingSpeakers\"), or both be left out (no 'Speakers' pass).";
            if (this.OngoingSpeakersMetadata != null && !this.OngoingSpeakersMetadata.StartsWith("Ongoing", StringComparison.OrdinalIgnoreCase))
                return $"'OngoingSpeakersMetadata' ('{this.OngoingSpeakersMetadata}') must start with 'Ongoing': only these metadata are carried to the next subtitles.";
            if (this.SpeakerMetadata != null && (this.SpeakerMetadata.Trim().Length == 0 || this.SpeakerMetadata.IndexOfAny(new[] { ':', '{', '}' }) >= 0))
                return $"'SpeakerMetadata' ('{this.SpeakerMetadata}') is not a valid metadata name.";
            return null;
        }

        protected override string GetMetadataProduced() => null;
        protected override string GetDefaultProduces() => "Review";
        protected override string GetDefaultVerb() => "Reviewing";

        protected override void DescribeDataFlow(WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            flow.InputMetadatas(this.Metadatas);
            flow.InputSources(this.TextSources, "text");
            flow.Output(DataKind.File, this.FileSuffix);
        }

        internal JobState GetJobState(WorkInProgressSubtitles wip)
            => wip.Tracks.FirstOrDefault(t => t.Id == this.Id)?.CurrentJobState as JobState;

        protected override IEnumerable<string> GetAdditionalStatusLines(SubtitleGeneratorContext context)
        {
            foreach (var line in base.GetAdditionalStatusLines(context))
            {
                yield return line;
            }

            // (the time of the user in the Review editor: from the job state, or from the cost added when the review was done)
            TimeSpan? activeTime = null;
            context.WIP.ExecuteLocked(() =>
            {
                var transcription = context.WIP.Tracks.FirstOrDefault(t => t.Id == this.Id);
                activeTime = (transcription?.CurrentJobState as JobState)?.ActiveTime
                    ?? transcription?.Costs.FirstOrDefault(c => c.EngineIdentifier == ToolName)?.TimeTaken;
            });
            if (activeTime != null)
            {
                yield return $"Review time = {(int)activeTime.Value.TotalHours}:{activeTime.Value:mm\\:ss} of active work in the Review editor";
            }
        }

        /// <summary>What the web UI offers to the user while the review is not done (see ITaskNode.SetUserAction).</summary>
        private string UserAction => $"timing:{this.Id}";

        private string WaitingReason => $"waiting for the review of '{this.FileSuffix}' in the Review editor of the web UI (button 'Review' of the scene), then 'Review done'";

        protected override bool IsPrerequisitesMet(SubtitleGeneratorContext context, out string reason)
        {
            reason = GetSpeakersConfigError();
            if (reason != null)
                return false;

            var jobState = GetJobState(context.WIP);
            if (jobState?.IsDone == true)
            {
                // (only the file to write)
                reason = null;
                return true;
            }
            if (jobState != null)
            {
                reason = this.WaitingReason;
                context.AddUserTodo($"Review '{this.FileSuffix}' in the Review editor, then click 'Review done'.");
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

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Track transcription)
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
                    context.AddUserTodo($"Review '{this.FileSuffix}' in the Review editor, then click 'Review done'.");
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
                var importerIds = GetImporters(context).Select(w => w.Id).ToArray();
                var alreadyReviewed = (markers.Length > 0 && !existingFile.Subtitles.Any(s => s.Lines.Any(l => markers.Any(m => l.Contains(m)))))
                    || context.WIP.Tracks.Any(t => importerIds.Contains(t.Id) && t.IsFinished);

                // (what was validated in the editor when it was editing the file directly)
                var validated = ReadValidatedFromOldEditor(context.WIP);
                return new JobState
                {
                    IsDone = alreadyReviewed,
                    FileAlreadyReviewed = alreadyReviewed,
                    Origin = $"imported from the file '{this.FileSuffix}'"
                        + (alreadyReviewed ? " (already reviewed)" : string.Empty)
                        + (validated.Count > 0 ? $", {validated.Count} validated" : string.Empty),
                    Items = existingFile.Subtitles.Select(s =>
                    {
                        var isValidated = validated.Contains($"{(long)s.StartTime.TotalMilliseconds}-{(long)s.EndTime.TotalMilliseconds}");
                        return new ReviewItem
                        {
                            StartTime = s.StartTime,
                            EndTime = s.EndTime,
                            Lines = SplitLines(s.Lines),
                            Finalized = isValidated
                        };
                    }).ToList()
                };
            }

            // Like OutputSrt (with UseBySourceMetadatas = false): the text of TextSources, then the metadata of the timing
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
                        // (several sources: the name of the source on its own line, so that the lines of the text stay as they are, ex. '[REVIEW:...]')
                        if (textSources.Length > 1)
                            lines.Add($"[{textSource.Container.Id}]");
                        lines.AddRange(SplitLines(new[] { text }));
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

        /// <summary>A metadata, like the importer reads them (see SubtitleWorker.ReadMetadataSubtitles): {Name:Value} anywhere in the text.</summary>
        private static readonly System.Text.RegularExpressions.Regex rs_metadataRegex =
            new System.Text.RegularExpressions.Regex(@"{(?<name>[^}:]*)(\:(?<value>[^}]*))?}");

        /// <summary>The value of a metadata of the subtitle ({Name:Value} anywhere in its text, the last one wins), or null.</summary>
        private static string GetMetadataValue(ReviewItem item, string name)
        {
            string value = null;
            foreach (System.Text.RegularExpressions.Match match in rs_metadataRegex.Matches(string.Join("\n", item.Lines ?? Array.Empty<string>())))
            {
                if (string.Equals(match.Groups["name"].Value.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    value = match.Groups["value"].Success ? match.Groups["value"].Value.Trim() : string.Empty;
            }
            return value;
        }

        /// <summary>
        /// The subtitles without {Speaker} where only one speaker is present ({OngoingSpeakers}, carried from the subtitles before):
        /// their speaker is known (like in the Timing editor), and it's written in the file, otherwise the next steps see no speaker
        /// (ex. TransformSubtitleConformer would take 2 subtitles of the same speaker, one with {Speaker} and one without, for a dialogue).
        /// Only when there are several speakers in the scene (with only one, nothing is added: there is no doubt).
        /// </summary>
        private Dictionary<ReviewItem, string> GetImplicitSpeakers(ReviewItem[] items)
        {
            var result = new Dictionary<ReviewItem, string>();
            if (!this.HasSpeakersPass)
                return result;

            static string[] SplitNames(string value) => (value ?? string.Empty).Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToArray();

            var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var present = Array.Empty<string>();
            var candidates = new List<(ReviewItem item, string speaker)>();
            foreach (var item in items)
            {
                // ({OngoingSpeakers} alone, empty: nobody present from there on, like the Timing editor and the AI requests read it)
                var presentValue = GetMetadataValue(item, this.OngoingSpeakersMetadata);
                if (presentValue != null)
                    present = SplitNames(presentValue);
                allNames.UnionWith(present);
                var speaker = GetMetadataValue(item, this.SpeakerMetadata);
                if (!string.IsNullOrEmpty(speaker))
                    allNames.Add(speaker);
                else if (present.Length == 1)
                    candidates.Add((item, present[0]));
            }
            if (allNames.Count >= 2)
            {
                foreach (var (item, speaker) in candidates)
                    result[item] = speaker;
            }
            return result;
        }

        /// <summary>A speaker name that can be in '{OngoingSpeakers:A, B}' / '{Speaker:A}': not empty, without ',', '{', '}' (null: valid).</summary>
        internal static string GetSpeakerNameError(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "A speaker name can't be empty.";
            if (name.IndexOfAny(new[] { ',', '{', '}', '\r', '\n' }) >= 0)
                return $"The speaker name '{name}' can't contain ',', '{{', '}}' or a line break.";
            return null;
        }

        /// <summary>The lines as they are in the .srt file (a line with line breaks is several lines).</summary>
        internal static string[] SplitLines(IEnumerable<string> lines)
            => (lines ?? Array.Empty<string>())
                .SelectMany(l => (l ?? string.Empty).Replace("\r\n", "\n").Split('\n', '\r'))
                .ToArray();

        // ---- Review done ----

        /// <summary>The workers that import the file (ex. InputImportMetadatas 'manual-input').</summary>
        private InputImportMetadatas[] GetImporters(SubtitleGeneratorContext context)
            => context.Config.Workers
                .OfType<InputImportMetadatas>()
                .Where(w => w.Enabled && string.Equals(w.FileSuffix, this.FileSuffix, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        /// <summary>The line that blocks the import of the file until the user reviewed it (ex. '[USER-REVISION-NEEDED]').</summary>
        private string[] GetImportMarkers(SubtitleGeneratorContext context)
            => GetImporters(context)
                .Where(w => !string.IsNullOrEmpty(w.ProcessOnlyWhenStringIsRemoved))
                .Select(w => w.ProcessOnlyWhenStringIsRemoved)
                .ToArray();

        private void WriteFileAndFinish(SubtitleGeneratorContext context, Track transcription, JobState jobState)
        {
            // (the review is done: without the line that blocks the import of the file)
            var markers = GetImportMarkers(context);

            ReviewItem[] items = null;
            context.WIP.ExecuteLocked(() => items = jobState.Items.OrderBy(i => i.StartTime).ToArray());

            // (how long the review took: in the first subtitle, as a text line, not a metadata)
            var activeTime = jobState.ActiveTime;
            var reviewTimeLine = $"(Review: {(int)activeTime.TotalHours}:{activeTime:mm\\:ss} of active work in the Review editor, {items.Length} subtitles, {items.Count(i => i.Finalized)} with the timing validated)";

            // (the speaker of the subtitles where only one speaker is present: written, see GetImplicitSpeakers)
            var implicitSpeakers = GetImplicitSpeakers(items);
            if (implicitSpeakers.Count > 0)
            {
                context.WriteInfo($"  {{{this.SpeakerMetadata}}} added to {implicitSpeakers.Count} subtitle(s) where only one speaker is present.");
            }

            var file = context.WIP.CreateVirtualSubtitleFile();
            foreach (var item in items)
            {
                var lines = (item.Lines ?? Array.Empty<string>()).Where(l => !markers.Any(m => l.Contains(m)));
                if (implicitSpeakers.TryGetValue(item, out var implicitSpeaker))
                    lines = lines.Concat(new[] { $"{{{this.SpeakerMetadata}:{implicitSpeaker}}}" });
                if (file.Subtitles.Count == 0 && activeTime > TimeSpan.Zero)
                    lines = lines.Concat(new[] { reviewTimeLine });
                file.Subtitles.Add(new Subtitle(item.StartTime, item.EndTime, lines.ToArray()));
            }
            if (activeTime > TimeSpan.Zero)
            {
                context.WriteInfo($"  Review of '{this.FileSuffix}': {(int)activeTime.TotalHours}:{activeTime:mm\\:ss} of active work in the Review editor.");
            }
            if (jobState.FileAlreadyReviewed && context.WIP.LoadVirtualSubtitleFile(this.FileSuffix) != null)
            {
                // (reviewed the old way: the file stays as it is)
                context.WriteInfo($"  '{this.FileSuffix}' was already reviewed: kept as it is.");
            }
            else
            {
                file.Save(context.WIP.ParentPath, this.FileSuffix, context.SoftDelete);
                context.WriteInfo($"  '{this.FileSuffix}' written ({items.Length} subtitles, {items.Count(i => i.Finalized)} with the timing validated).");
            }

            context.WIP.ExecuteLocked(() =>
            {
                transcription.Items.Clear();
                transcription.Items.AddRange(
                    ReadMetadataSubtitles(file.Subtitles)
                    .Select(f => new TimedItemWithMetadata(f.StartTime, f.EndTime, f.Metadata)));
                // (the time of the user, in the costs: like the other interactive workers)
                if (activeTime > TimeSpan.Zero && !transcription.Costs.Any(c => c.EngineIdentifier == ToolName))
                    transcription.Costs.Add(new Cost(transcription.Id, ToolName, activeTime, items.Length));
                transcription.MarkAsFinished();
            });
            context.WIP.Save();
        }
    }
}
