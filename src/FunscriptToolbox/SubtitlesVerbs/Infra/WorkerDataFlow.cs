using FunscriptToolbox.SubtitlesVerbs.Progress;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using FunscriptToolbox.SubtitlesVerbs.Translations;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    /// <summary>The kinds of data that a worker can use / produce (shown in the 'Flow' view of the web UI).</summary>
    public static class DataKind
    {
        /// <summary>The video(s) of the scene.</summary>
        public const string Video = "video";
        /// <summary>An audio extraction (PCM in the .wipsubtitles), by its id.</summary>
        public const string Audio = "audio";
        /// <summary>A transcription, by its id.</summary>
        public const string Transcription = "transcription";
        /// <summary>A translation, by its id.</summary>
        public const string Translation = "translation";
        /// <summary>A file next to the video, by its suffix (ex. ".manual-input.srt").</summary>
        public const string File = "file";
        /// <summary>(input only) Transcription(s) / translation(s) given by a rule, ex. "singlevad-*" (resolved by the graph).</summary>
        public const string Sources = "sources";
    }

    /// <summary>One data used / produced by a worker.</summary>
    public class DataRef
    {
        public string Kind { get; set; }
        public string Id { get; set; }
        /// <summary>What the data is for, shown on the arrow (ex. "timings", "metadata", "clips").</summary>
        public string Role { get; set; }
    }

    /// <summary>What a worker uses and produces (see SubtitleWorker.DescribeDataFlow).</summary>
    public class WorkerDataFlow
    {
        public List<DataRef> Inputs { get; } = new List<DataRef>();
        public List<DataRef> Outputs { get; } = new List<DataRef>();

        public void Input(string kind, string id, string role = null)
        {
            if (!string.IsNullOrWhiteSpace(id))
                Inputs.Add(new DataRef { Kind = kind, Id = id.Trim(), Role = role });
        }

        public void Output(string kind, string id, string role = null)
        {
            if (!string.IsNullOrWhiteSpace(id))
                Outputs.Add(new DataRef { Kind = kind, Id = id.Trim(), Role = role });
        }

        /// <summary>A list of source rules (ex. "full-whisper, singlevad-*"), resolved like the run does (see MetadataAggregator).</summary>
        public void InputSources(string sources, string role)
        {
            foreach (var rule in (sources ?? string.Empty).Split(',').Select(f => f.Trim()).Where(f => f.Length > 0))
            {
                Input(DataKind.Sources, rule, role);
            }
        }

        /// <summary>The timings and the metadata sources of a MetadataAggregator (null: nothing).</summary>
        public void InputMetadatas(MetadataAggregator metadatas)
        {
            if (metadatas == null)
                return;
            InputSources(metadatas.TimingsSource, "timings");
            InputSources(metadatas.Sources, "metadata");
        }

        /// <summary>The binary data sent to an AI (the enabled extractors): audio clips from an audio extraction, screenshots from the video.</summary>
        public void InputExtractors(IEnumerable<BinaryDataExtractor> extractors)
        {
            foreach (var extractor in extractors ?? Array.Empty<BinaryDataExtractor>())
            {
                if (extractor == null || !extractor.Enabled)
                    continue;
                if (extractor is BinaryDataExtractorAudio audio)
                    Input(DataKind.Audio, audio.SourceAudioId, "clips");
                else if (extractor is BinaryDataExtractorImage)
                    Input(DataKind.Video, "video", "screenshots");
            }
        }
    }

    /// <summary>
    /// The graph of the workers of a scene and of the data between them (the 'Flow' view of the web UI):
    /// built from the configuration used by the scene, the states come from the task tree (node ids).
    /// </summary>
    public static class SceneFlowGraph
    {
        /// <summary>
        /// The workers of the graph: the key of a worker ("w3") is its index in this array.
        /// (not the aliases: the steps that use one are linked to its source, see Build)
        /// </summary>
        internal static SubtitleWorker[] GetWorkers(SubtitleGeneratorConfig config) =>
            (config?.Workers ?? Array.Empty<SubtitleWorker>()).Where(w => w != null && !(w is TranscriberAlias)).ToArray();

        public static object Build(SubtitleGeneratorConfig config, Func<SubtitleWorker, ITaskNode> getWorkerNode)
        {
            var workers = GetWorkers(config);
            var flows = workers.Select(w => w.GetDataFlow()).ToArray();

            var workerItems = new List<object>();
            var dataItems = new Dictionary<string, object>();
            var edges = new List<object>();

            // The transcriptions / translations produced by the workers (for the source rules, ex. "singlevad-*")
            var producedIds = new List<(string kind, string id)>();
            foreach (var flow in flows)
                foreach (var output in flow.Outputs.Where(o => o.Kind == DataKind.Transcription || o.Kind == DataKind.Translation))
                    producedIds.Add((output.Kind, output.Id));

            // The enabled aliases: their source (null: they loop)
            var aliasTargets = new Dictionary<string, string>();
            foreach (var alias in TranscriberAlias.GetAliases(config))
            {
                try { aliasTargets[alias.TranscriptionId] = TranscriberAlias.Resolve(config, alias.TranscriptionId); }
                catch (InvalidOperationException) { aliasTargets[alias.TranscriptionId] = null; }
            }

            string DataKey(string kind, string id) => $"{kind}:{id}";
            void AddData(string kind, string id)
            {
                var key = DataKey(kind, id);
                if (!dataItems.ContainsKey(key))
                    dataItems[key] = new { key, kind, name = id };
            }

            for (var index = 0; index < workers.Length; index++)
            {
                var worker = workers[index];
                var flow = flows[index];
                var workerKey = $"w{index}";
                workerItems.Add(new
                {
                    key = workerKey,
                    name = worker.DisplayName,
                    type = worker.GetType().Name,
                    enabled = worker.Enabled,
                    order = index,
                    nodeId = worker.Enabled ? getWorkerNode(worker)?.Id : null
                });

                var ownOutputs = new HashSet<string>(flow.Outputs.Select(o => DataKey(o.Kind, o.Id)));
                foreach (var output in flow.Outputs)
                {
                    AddData(output.Kind, output.Id);
                    edges.Add(new { from = workerKey, to = DataKey(output.Kind, output.Id), role = output.Role });
                }

                foreach (var input in flow.Inputs)
                {
                    var keys = new List<string>();
                    var viaAlias = new Dictionary<string, string>();   // key => the alias it was reached with
                    if (input.Kind == DataKind.Sources)
                    {
                        // Same rule as the run: a transcription / translation whose id matches
                        var regex = MetadataAggregator.ToSourceRegex(input.Id);
                        foreach (var (kind, id) in producedIds.Distinct().Where(p => regex.IsMatch(p.id)))
                        {
                            AddData(kind, id);
                            keys.Add(DataKey(kind, id));
                        }
                        // An alias that matches: its source (once, even if it's also matched by its own id)
                        foreach (var alias in aliasTargets.Where(a => regex.IsMatch(a.Key)))
                        {
                            var target = producedIds.Distinct().FirstOrDefault(p => p.id == alias.Value);
                            if (target.id == null)
                            {
                                // (its source is not produced in this configuration, or the aliases loop: shown as is)
                                AddData(DataKind.Sources, alias.Key);
                                keys.Add(DataKey(DataKind.Sources, alias.Key));
                                continue;
                            }
                            var targetKey = DataKey(target.kind, target.id);
                            AddData(target.kind, target.id);
                            if (!keys.Contains(targetKey))
                            {
                                keys.Add(targetKey);
                                viaAlias[targetKey] = alias.Key;
                            }
                        }
                        if (keys.Count == 0)
                        {
                            // (nothing in this configuration matches the rule: shown as is)
                            AddData(DataKind.Sources, input.Id);
                            keys.Add(DataKey(DataKind.Sources, input.Id));
                        }
                    }
                    else
                    {
                        AddData(input.Kind, input.Id);
                        keys.Add(DataKey(input.Kind, input.Id));
                    }

                    foreach (var key in keys.Where(k => !ownOutputs.Contains(k)))   // (a rule like "*" also matches the worker's own output)
                    {
                        var role = viaAlias.TryGetValue(key, out var aliasName)
                            ? (input.Role == null ? $"as '{aliasName}'" : $"{input.Role} (as '{aliasName}')")
                            : input.Role;
                        edges.Add(new { from = key, to = workerKey, role });
                    }
                }
            }

            return new
            {
                workers = workerItems,
                data = dataItems.Values.ToArray(),
                edges = edges.Distinct().ToArray()
            };
        }
    }

    /// <summary>
    /// The inspector of the 'Flow' view: the items of a box of the graph (a worker: what it receives and what it produced;
    /// a transcription / translation: its items). Read on demand, from the configuration and the .wipsubtitles of the scene.
    /// </summary>
    public static class SceneFlowInspector
    {
        private const int MaxItems = 5000;

        /// <summary>
        /// The number of items of each transcription / translation (key of the graph, ex. "transcription:full-whisper"), and if it's finished:
        /// shown in a corner of their box. (only the sizes of the lists are read: no lock needed, the scene can be adding items)
        /// And the AI cost of each step that has AI calls (its key, ex. "w3": { cost, calls, fallbacks }), in a corner of its box
        /// (the costs are copied with the lock of the .wipsubtitles, see SubtitleOutputCostReport.GetAICostByWorker).
        /// </summary>
        public static Dictionary<string, object> CountItems(SubtitleGeneratorContext context)
        {
            var counts = new Dictionary<string, object>();
            var wip = context.WIP;
            if (wip == null)
                return counts;
            foreach (var t in wip.Transcriptions.ToArray())
                counts[$"{DataKind.Transcription}:{t.Id}"] = new { n = t.Items.Count, done = t.IsFinished };
            foreach (var t in wip.Translations.ToArray())
                counts[$"{DataKind.Translation}:{t.Id}"] = new { n = t.Items.Count, done = t.IsFinished };

            // The AI cost of each step (its key, ex. "w3"), estimate in $ like the 'costs' report: a pill on its box
            var workers = SceneFlowGraph.GetWorkers(context.Config);
            var outputs = workers.Select(w =>
            {
                var flowOutputs = w.GetDataFlow().Outputs;
                return (w,
                    (IReadOnlyCollection<string>)flowOutputs.Where(o => o.Kind == DataKind.Transcription).Select(o => o.Id).ToHashSet(),
                    (IReadOnlyCollection<string>)flowOutputs.Where(o => o.Kind == DataKind.Translation).Select(o => o.Id).ToHashSet());
            }).ToList();
            var costs = Outputs.SubtitleOutputCostReport.GetAICostByWorker(wip, outputs);
            for (var index = 0; index < workers.Length; index++)
            {
                if (costs.TryGetValue(workers[index], out var cost))
                    counts[$"w{index}"] = new { cost = Math.Round(cost.Dollars, 4), calls = cost.Calls, fallbacks = cost.Fallbacks };
            }
            return counts;
        }

        public static object Inspect(SubtitleGeneratorContext context, string key)
        {
            var config = context.Config;
            var wip = context.WIP;
            if (config == null || wip == null)
                return new { title = key, note = "The scene is not loaded (yet).", sections = Array.Empty<object>() };

            // A worker ("w3": its index in the workers of the configuration)
            if (key.StartsWith("w") && int.TryParse(key.Substring(1), out var index))
            {
                var workers = SceneFlowGraph.GetWorkers(config);
                if (index < 0 || index >= workers.Length)
                    return new { title = key, note = "Unknown worker.", sections = Array.Empty<object>() };
                return InspectWorker(context, workers[index]);
            }

            // A data ("transcription:full-whisper", "translation:...", "audio:...", "file:...")
            var colon = key.IndexOf(':');
            var kind = colon > 0 ? key.Substring(0, colon) : key;
            var id = colon > 0 ? key.Substring(colon + 1) : string.Empty;
            switch (kind)
            {
                case DataKind.Transcription:
                case DataKind.Translation:
                    var container = FindContainer(wip, kind, id);
                    // (the worker that produces it: its screenshots, sent to the AI, can be shown for each item too)
                    var allWorkers = SceneFlowGraph.GetWorkers(config);
                    var producerIndex = Array.FindIndex(allWorkers, w => w.Enabled
                        && w.GetDataFlow().Outputs.Any(o => o.Kind == kind && o.Id == id));
                    return new
                    {
                        title = $"{kind} '{id}'",
                        note = container == null ? "Not produced yet." : (container.IsFinished ? null : "Not finished yet."),
                        images = producerIndex >= 0 ? GetImageExtractors(allWorkers[producerIndex]).Select(e => e.OutputFieldName).ToArray() : Array.Empty<string>(),
                        imageWorker = producerIndex >= 0 ? $"w{producerIndex}" : null,
                        contextImages = producerIndex >= 0 ? GetContextImageTimes(context, allWorkers[producerIndex]) : null,
                        sections = container == null ? Array.Empty<object>() : new[] { Section($"Items of {kind} '{id}'", ReadItems(wip, container), null) }
                    };
                case DataKind.Audio:
                    var audio = wip.AudioExtractions.FirstOrDefault(a => a.Id == id);
                    return new
                    {
                        title = $"audio '{id}'",
                        note = audio?.IsFinished == true ? $"Audio extraction, duration: {audio.PcmAudio?.Duration:hh\\:mm\\:ss\\.fff} (no items)." : "Not extracted yet.",
                        sections = Array.Empty<object>()
                    };
                default:
                    return new { title = $"{kind} '{id}'", note = kind == DataKind.File || kind == DataKind.Video ? "On disk (no items to show)." : "No items to show.", sections = Array.Empty<object>() };
            }
        }

        private static object InspectWorker(SubtitleGeneratorContext context, SubtitleWorker worker)
        {
            var wip = context.WIP;
            var flow = worker.GetDataFlow();
            var sections = new List<object>();

            // Produced: its transcription / translation (the items, with their metadata)
            var produced = flow.Outputs
                .Where(o => o.Kind == DataKind.Transcription || o.Kind == DataKind.Translation)
                .Select(o => (o, container: FindContainer(wip, o.Kind, o.Id)))
                .ToArray();
            var producedItems = produced.Where(p => p.container != null).SelectMany(p => ReadItems(wip, p.container)).ToArray();

            // Received: the items merged from its 'Metadatas' (timings + metadata of its sources), what the worker works on
            // ('to do': not in what it produced yet)
            var metadatas = worker.GetType().GetProperty("Metadatas", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(worker) as MetadataAggregator;
            if (metadatas != null)
            {
                var title = $"Received: timings of '{metadatas.TimingsSource}'" + (string.IsNullOrWhiteSpace(metadatas.Sources) ? string.Empty : $", metadata of '{metadatas.Sources}'");
                var received = Retry(() =>
                {
                    MetadataAggregation aggregation = null;
                    wip.ExecuteLocked(() => aggregation = metadatas.Aggregate(context));
                    return aggregation;
                });
                if (received?.ReferenceTimingsWithMetadata == null)
                {
                    sections.Add(Section(title, Array.Empty<object>(), $"Not available yet: '{metadatas.TimingsSource}' is not finished."));
                }
                else
                {
                    var producedStarts = new HashSet<TimeSpan>(produced.Where(p => p.container != null).SelectMany(p => SafeItems(wip, p.container)).Select(i => i.StartTime));
                    var items = received.ReferenceTimingsWithMetadata.Take(MaxItems)
                        .Select(i => ToItem(i, todo: produced.Length > 0 && !producedStarts.Contains(i.StartTime)))
                        .ToArray();
                    sections.Add(Section(title, items, received.ReferenceTimingsWithMetadata.Length > MaxItems ? $"Only the first {MaxItems} items." : null));
                }
            }

            foreach (var (output, container) in produced)
            {
                sections.Add(container == null
                    ? Section($"Produced: {output.Kind} '{output.Id}'", Array.Empty<object>(), "Nothing produced yet.")
                    : Section($"Produced: {output.Kind} '{output.Id}'" + (container.IsFinished ? string.Empty : " (not finished)"), ReadItems(wip, container), null));
            }

            var inputs = flow.Inputs.Select(i => $"{i.Kind} '{i.Id}'" + (i.Role != null ? $" ({i.Role})" : string.Empty)).Distinct().ToArray();
            var outputs = flow.Outputs.Select(o => $"{o.Kind} '{o.Id}'").Distinct().ToArray();
            return new
            {
                title = worker.DisplayName,
                type = worker.GetType().Name,
                enabled = worker.Enabled,
                inputs,
                outputs,
                // The screenshots sent to the AI, by item (a column of the tables: the image is read when asked, see GetImage)
                images = GetImageExtractors(worker).Select(e => e.OutputFieldName).ToArray(),
                contextImages = GetContextImageTimes(context, worker),
                note = sections.Count == 0 ? "This worker has no items to show (it works on audio or files)." : null,
                sections,
                // (what it produced, kept under another id ('<id>-OLD') by 'Redo this step', then the scene is started again: see the main loop)
                redo = produced.Where(p => p.container != null).Select(p => p.container.Id).FirstOrDefault()
            };
        }

        /// <summary>
        /// The times of the screenshots of the gap before each item ('context only' nodes, see BinaryDataExtractor.AddContextNodes),
        /// by the start of the item ("hh:mm:ss.fff"): [{ field, times }]; null: none. Computed like the requests (AIRequestGenerator):
        /// the gap from the end of the previous item of the timings of the worker (0 for the first one) to the start of the item.
        /// (the field is a value, not a key: the keys of a dictionary are changed by the camelCase of the JSON of the web UI)
        /// </summary>
        private static Dictionary<string, object[]> GetContextImageTimes(SubtitleGeneratorContext context, SubtitleWorker worker)
        {
            var extractors = GetImageExtractors(worker).Where(e => e.AddContextNodes).ToArray();
            if (extractors.Length == 0)
                return null;
            try
            {
                var aggregator = worker.GetType().GetProperty("Metadatas", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(worker) as MetadataAggregator;
                var timings = aggregator?.Aggregate(context).ReferenceTimingsWithMetadata;
                if (timings == null)
                    return null;
                var result = new Dictionary<string, object[]>();
                var previousEndTime = TimeSpan.Zero;
                foreach (var item in timings)
                {
                    var gap = new Timing(previousEndTime, item.StartTime);
                    var byField = extractors
                        .Select(e => (field: e.OutputFieldName, times: e.GetContextTimes(gap).Select(t => t.ToString(@"hh\:mm\:ss\.fff")).ToArray()))
                        .Where(x => x.times.Length > 0)
                        .Select(x => (object)new { x.field, x.times })
                        .ToArray();
                    if (byField.Length > 0)
                        result[item.StartTime.ToString(@"hh\:mm\:ss\.fff")] = byField;
                    previousEndTime = item.EndTime;
                }
                return result;
            }
            catch (Exception)
            {
                // (only to show the screenshots of the gaps: not shown this time, ex. the timings are not there yet)
                return null;
            }
        }

        /// <summary>The enabled screenshot extractors of a worker (its 'Options' of AI worker), or nothing.</summary>
        private static BinaryDataExtractorImage[] GetImageExtractors(SubtitleWorker worker)
        {
            var options = worker.GetType().GetProperty("Options", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(worker) as AIOptions;
            return (options?.BinaryDataExtractors ?? Array.Empty<BinaryDataExtractor>())
                .OfType<BinaryDataExtractorImage>()
                .Where(e => e.Enabled)
                .ToArray();
        }

        // The screenshots already read (by file + time): taking one with ffmpeg takes a moment, and the same ones are looked at again
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> rs_images = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>();
        private const int MaxImagesInMemory = 300;

        /// <summary>
        /// The screenshot that a worker sends to the AI for an item (the 'images' of the inspector): the same one as the run
        /// (middle of the item, same file of the .vseq, same ffmpeg filter). Read from the temporary file of the run if it was kept
        /// (KeepTemporaryFiles), otherwise taken with ffmpeg (with the same limit as the run: one at a time on the video). Null: none.
        /// </summary>
        /// <param name="onlyIfAvailable">True: only when it's in memory or on disk, never taken with ffmpeg (ex. the thumbnails of a table: null otherwise).</param>
        public static byte[] GetImage(SubtitleGeneratorContext context, string workerKey, string field, TimeSpan start, TimeSpan end, bool onlyIfAvailable = false)
        {
            var config = context.Config;
            var wip = context.WIP;
            if (config == null || wip == null || !workerKey.StartsWith("w") || !int.TryParse(workerKey.Substring(1), out var index))
                return null;
            var workers = SceneFlowGraph.GetWorkers(config);
            if (index < 0 || index >= workers.Length)
                return null;
            var extractor = GetImageExtractors(workers[index]).FirstOrDefault(e => e.OutputFieldName == field);
            if (extractor == null)
                return null;

            var middleTime = TimeSpan.FromMilliseconds((start.TotalMilliseconds + end.TotalMilliseconds) / 2);
            var (_, filename, middleTimeInRightFile) = wip.TimelineMap.GetPathAndPosition(middleTime);
            var videoPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(wip.ParentPath, filename));
            var filter = extractor.FfmpegFilter?.Replace("[STARTTIME]", context.FfmpegHelper.EscapeFfmpegDrawtext(start.ToString(@"hh\:mm\:ss\.fff")));
            var cacheKey = $"{videoPath}|{middleTimeInRightFile.Ticks}|{filter}";
            if (rs_images.TryGetValue(cacheKey, out var cached))
                return cached;

            // 1. The temporary file of the run (same name as in TranscriberAI)
            var backupFilePath = context.GetPotentialVerboseFilePath($"{System.IO.Path.GetFileNameWithoutExtension(filename)}_{extractor.OutputFieldName}_{middleTimeInRightFile:hh\\-mm\\-ss\\-fff}.jpg");
            if (onlyIfAvailable && !System.IO.File.Exists(backupFilePath))
                return null;
            var image = System.IO.File.Exists(backupFilePath)
                ? System.IO.File.ReadAllBytes(backupFilePath)
                // 2. Otherwise, taken now with ffmpeg
                : context.FfmpegHelper.TakeScreenshotAsBytes(videoPath, middleTimeInRightFile, ".jpg", filter);

            if (image != null)
            {
                if (rs_images.Count >= MaxImagesInMemory) rs_images.Clear();
                rs_images[cacheKey] = image;
            }
            return image;
        }

        private static TimedItemWithMetadataCollection FindContainer(WorkInProgressSubtitles wip, string kind, string id)
        {
            return kind == DataKind.Transcription
                ? (TimedItemWithMetadataCollection)wip.Transcriptions.FirstOrDefault(t => t.Id == id)
                : wip.Translations.FirstOrDefault(t => t.Id == id);
        }

        private static object Section(string name, object[] items, string note) => new { name, items, note };

        private static object ToItem(TimedItemWithMetadata item, bool todo = false) => new
        {
            s = item.StartTime.ToString(@"hh\:mm\:ss\.fff"),
            e = item.EndTime.ToString(@"hh\:mm\:ss\.fff"),
            m = item.Metadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            todo
        };

        private static object[] ReadItems(WorkInProgressSubtitles wip, TimedItemWithMetadataCollection container) =>
            SafeItems(wip, container).Take(MaxItems).Select(i => ToItem(i)).ToArray();

        /// <summary>
        /// A copy of the items: the scene can be working on them right now (on its own thread). The copy is made while no
        /// save is in progress; if the list changes during the copy, it's tried again.
        /// </summary>
        private static TimedItemWithMetadata[] SafeItems(WorkInProgressSubtitles wip, TimedItemWithMetadataCollection container)
        {
            return Retry(() =>
            {
                TimedItemWithMetadata[] copy = null;
                wip.ExecuteLocked(() => copy = container.GetItems().ToArray());
                return copy;
            }) ?? Array.Empty<TimedItemWithMetadata>();
        }

        private static T Retry<T>(Func<T> read) where T : class
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return read();
                }
                catch (InvalidOperationException) when (attempt < 5)
                {
                    // (a list changed during the read: again)
                    System.Threading.Thread.Sleep(20);
                }
            }
        }
    }
}
