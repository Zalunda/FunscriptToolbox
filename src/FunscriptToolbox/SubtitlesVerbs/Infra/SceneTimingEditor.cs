using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Progress;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner;
using System;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    /// <summary>
    /// The data of the 'Timing' editor of the web UI, for a scene (see <see cref="ITimingEditor"/>).
    /// What can be edited comes from the configuration of the scene:
    /// - the reviews: the ones of the enabled 'TranscriberInteractifReview' (their subtitles are in the job state of their transcription,
    ///   in the .wipsubtitles; the file, ex. '.manual-input.srt', is written by the worker when the review is done),
    /// - the terrain settings: the ones of the enabled 'TranscriberAIManualInputPreparer' that refine the timings (for their audio).
    /// The context of the scene is read each time (the scene can be loaded again, ex. 'Retry').
    /// </summary>
    internal class SceneTimingEditor : ITimingEditor
    {
        /// <summary>Suffix of the light video of a part, next to it, replacing its extension (ex. "video.FAST.mp4"), like the 'Speakers' UI.</summary>
        public const string LightVideoSuffix = ".FAST.mp4";

        private const int MaxNbBins = 8000;

        private readonly SubtitleGeneratorContext r_context;
        private readonly object r_terrainLock = new object();
        private readonly Dictionary<string, Task<AudioTerrainMapping>> r_terrains = new Dictionary<string, Task<AudioTerrainMapping>>();

        public SceneTimingEditor(SubtitleGeneratorContext context)
        {
            r_context = context;
        }

        private WorkInProgressSubtitles Wip => r_context.WIP
            ?? throw new TimingEditorException("The scene is not loaded (yet).");

        private SubtitleGeneratorConfig Config => r_context.Config
            ?? throw new TimingEditorException("The configuration of the scene is not loaded (yet).");

        // ---- What can be edited / played ----

        private IEnumerable<TranscriberInteractifReview> GetReviewWorkers()
            => (Config.Workers ?? Array.Empty<SubtitleWorker>())
                .OfType<TranscriberInteractifReview>()
                .Where(w => w.Enabled);

        private IEnumerable<TranscriberAIManualInputPreparer> GetTerrainWorkers()
            => (Config.Workers ?? Array.Empty<SubtitleWorker>())
                .OfType<TranscriberAIManualInputPreparer>()
                .Where(w => w.Enabled && w.RefineTiming?.Enabled == true && !string.IsNullOrEmpty(w.RefineTiming.SourceAudioIdForTerrain));

        /// <summary>The parts of the scene, with known durations (otherwise the subtitles can't be placed in the time of the scene).</summary>
        private TimelineSegment[] GetParts(WorkInProgressSubtitles wip)
        {
            var map = wip.TimelineMap;
            if (map?.Segments == null || map.Segments.Length == 0)
                throw new TimingEditorException("The scene has no video part.");
            if (map.Duration <= TimeSpan.Zero)
                throw new TimingEditorException("The duration of the video(s) is not known yet (the audio is not extracted yet).");
            return map.Segments;
        }

        private TranscriberInteractifReview GetReviewWorker(string reviewId)
            => GetReviewWorkers().FirstOrDefault(w => w.TranscriptionId == reviewId)
                ?? throw new TimingEditorException($"'{reviewId}' is not a review of this scene (TranscriberInteractifReview).");

        private AudioExtraction[] GetAudioExtractions(WorkInProgressSubtitles wip)
        {
            AudioExtraction[] result = null;
            // (the scene can be adding an audio extraction right now)
            for (var attempt = 0; result == null; attempt++)
            {
                try
                {
                    wip.ExecuteLocked(() => result = wip.AudioExtractions?.ToArray() ?? Array.Empty<AudioExtraction>());
                }
                catch (InvalidOperationException) when (attempt < 3) { }
            }
            return result;
        }

        private PcmAudio GetPcmAudio(string audioId)
        {
            var extraction = GetAudioExtractions(Wip).FirstOrDefault(a => a.Id == audioId)
                ?? throw new TimingEditorException($"Unknown audio '{audioId}'.");
            return extraction.IsFinished
                ? extraction.PcmAudio
                : throw new TimingEditorException($"The audio '{audioId}' is not extracted yet.");
        }

        public object GetInfo()
        {
            var wip = Wip;
            var parts = GetParts(wip);
            var terrainWorkers = GetTerrainWorkers().ToArray();
            return new
            {
                durationMs = (long)wip.TimelineMap.Duration.TotalMilliseconds,
                parts = parts.Select((p, index) => new
                {
                    index,
                    filename = p.Filename,
                    offsetMs = (long)p.Offset.TotalMilliseconds,
                    durationMs = (long)p.Duration.TotalMilliseconds,
                    // "light": its .FAST.mp4; "original": the video itself (no light video: can be heavy to play); null: none
                    video = GetPlayedVideo(wip, p, false).kind,
                    // (the videos there are: the user chooses which one is played)
                    hasLight = File.Exists(GetLightVideoFullPath(wip, p)),
                    hasOriginal = File.Exists(Path.Combine(wip.ParentPath, p.Filename))
                }).ToArray(),
                lightVideoSuffix = LightVideoSuffix,
                reviews = GetReviewWorkers().Select(w =>
                {
                    TranscriberInteractifReview.JobState jobState = null;
                    wip.ExecuteLocked(() => jobState = w.GetJobState(wip));
                    return new
                    {
                        id = w.TranscriptionId,
                        worker = w.DisplayName,
                        fileSuffix = w.FileSuffix,
                        state = jobState == null ? "not started" : jobState.IsDone ? "done" : "to review"
                    };
                }).ToArray(),
                audios = GetAudioExtractions(wip).Select(a => new
                {
                    id = a.Id,
                    finished = a.IsFinished
                }).ToArray(),
                // (the audio of the terrain of the preparer: the one the timings were refined with)
                terrainSources = terrainWorkers.Select(w => new
                {
                    audioId = w.RefineTiming.SourceAudioIdForTerrain,
                    worker = w.DisplayName,
                    valleyReach = w.RefineTiming.ValleyReach
                }).ToArray()
            };
        }

        private static string GetLightVideoFullPath(WorkInProgressSubtitles wip, TimelineSegment part)
            => Path.ChangeExtension(Path.Combine(wip.ParentPath, part.Filename), LightVideoSuffix);

        /// <summary>
        /// The video played for a part: its light video ('.FAST.mp4', made by the 'fast-video' output), otherwise the video itself
        /// (ex. a 2D video doesn't need a light one; a big one, ex. 8K, can be choppy, and the browser may not be able to play its codec).
        /// preferOriginal (chosen by the user, ex. for a sharper image): the video itself first, otherwise its light video.
        /// </summary>
        private static (string path, string kind) GetPlayedVideo(WorkInProgressSubtitles wip, TimelineSegment part, bool preferOriginal)
        {
            var light = GetLightVideoFullPath(wip, part);
            var original = Path.Combine(wip.ParentPath, part.Filename);
            if (preferOriginal && File.Exists(original))
                return (original, "original");
            if (File.Exists(light))
                return (light, "light");
            if (File.Exists(original))
                return (original, "original");
            return (null, null);
        }

        public string GetVideoPath(int partIndex, bool original)
        {
            var wip = Wip;
            var parts = GetParts(wip);
            if (partIndex < 0 || partIndex >= parts.Length)
                return null;
            return GetPlayedVideo(wip, parts[partIndex], original).path;
        }

        // ---- The subtitles ----

        // (the subtitles of a review are in the job state of its transcription: read / changed while no save of the .wipsubtitles is in progress)

        private static string KeyOf(TimeSpan start, TimeSpan end) => $"{(long)start.TotalMilliseconds}-{(long)end.TotalMilliseconds}";

        private static string StampOf(TranscriberInteractifReview.JobState jobState) => $"v{jobState.Version}";

        /// <summary>Changes the job state of a review (not done), then saves the .wipsubtitles.</summary>
        private T ChangeReview<T>(string reviewId, Func<TranscriberInteractifReview.JobState, T> change)
        {
            var wip = Wip;
            var worker = GetReviewWorker(reviewId);
            T result = default;
            wip.ExecuteLocked(() =>
            {
                var jobState = worker.GetJobState(wip)
                    ?? throw new TimingEditorException("The review hasn't started yet.");
                if (jobState.IsDone)
                    throw new TimingEditorException("The review is done: it can't be changed here anymore (the file can still be edited by hand: its metadata are always used, its timings only until they are frozen by the next steps).");
                result = change(jobState);
            });
            SaveWip(wip);
            return result;
        }

        private static void SaveWip(WorkInProgressSubtitles wip)
        {
            // (the scene can be changing something in it right now: tried again)
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    wip.Save();
                    return;
                }
                catch (InvalidOperationException) when (attempt < 5)
                {
                    System.Threading.Thread.Sleep(200);
                }
            }
        }

        public object LoadSubtitles(string reviewId)
        {
            var wip = Wip;
            var worker = GetReviewWorker(reviewId);
            object result = null;
            wip.ExecuteLocked(() =>
            {
                var jobState = worker.GetJobState(wip);
                if (jobState == null)
                    return;
                result = new
                {
                    stamp = StampOf(jobState),
                    done = jobState.IsDone,
                    origin = jobState.Origin,
                    activeMs = (long)jobState.ActiveTime.TotalMilliseconds,
                    fileSuffix = worker.FileSuffix,
                    // The 'Speakers' pass: the metadata to set (null: no 'Speakers' pass), and the list of the speakers
                    speakersConfig = worker.HasSpeakersPass
                        ? new { speaker = worker.SpeakerMetadata, ongoing = worker.OngoingSpeakersMetadata }
                        : null,
                    speakers = (jobState.Speakers ?? new List<TranscriberInteractifReview.ReviewSpeaker>()).Select(sp => new { name = sp.Name, alias = sp.Alias, image = sp.Image }).ToArray(),
                    // (the parts listened to, to validate the gaps)
                    gaps = (jobState.ValidatedGaps ?? new List<TranscriberInteractifReview.ReviewTimeRange>())
                        .Select(r => new { s = (long)r.Start.TotalMilliseconds, e = (long)r.End.TotalMilliseconds }).ToArray(),
                    subtitles = jobState.Items.OrderBy(i => i.StartTime).Select(i => new
                    {
                        s = (long)i.StartTime.TotalMilliseconds,
                        e = (long)i.EndTime.TotalMilliseconds,
                        lines = i.Lines ?? Array.Empty<string>(),
                        // (its timing finalized by the user)
                        f = i.Finalized,
                        // (its speaker chosen / confirmed by the user, ex. {Speaker:Multiple} from the AI: to do until then)
                        sc = i.SpeakerConfirmed
                    }).ToArray()
                };
            });
            return result;
        }

        public void AddActiveTime(string reviewId, long ms)
        {
            if (ms <= 0 || ms > 10 * 60 * 1000)
                throw new TimingEditorException($"Invalid active time: {ms} ms.");
            ChangeReview(reviewId, jobState => jobState.ActiveTime += TimeSpan.FromMilliseconds(ms));
        }

        public void SaveValidatedGaps(string reviewId, TimingEditorRange[] ranges)
        {
            if (ranges == null)
                throw new TimingEditorException("No ranges received.");
            if (ranges.Length > 100000)
                throw new TimingEditorException($"Too many ranges: {ranges.Length}.");
            foreach (var r in ranges)
            {
                if (r == null || r.S < 0 || r.E <= r.S)
                    throw new TimingEditorException($"Invalid range: {r?.S}-{r?.E} ms.");
            }
            // (sorted, the ones that touch / overlap merged)
            var merged = new List<TranscriberInteractifReview.ReviewTimeRange>();
            foreach (var r in ranges.OrderBy(r => r.S))
            {
                var last = merged.LastOrDefault();
                if (last != null && r.S <= (long)last.End.TotalMilliseconds)
                {
                    if (r.E > (long)last.End.TotalMilliseconds)
                        last.End = TimeSpan.FromMilliseconds(r.E);
                }
                else
                {
                    merged.Add(new TranscriberInteractifReview.ReviewTimeRange { Start = TimeSpan.FromMilliseconds(r.S), End = TimeSpan.FromMilliseconds(r.E) });
                }
            }
            ChangeReview(reviewId, jobState => jobState.ValidatedGaps = merged);
        }

        public void MarkReviewDone(string reviewId)
        {
            ChangeReview(reviewId, jobState => jobState.IsDone = true);
        }

        /// <summary>
        /// What the user finalized: the subtitles known by their timing as saved ("start ms-end ms"). The other subtitles are not changed
        /// (ex. their timing changed in the page and isn't saved yet: whether they are finalized is saved with them, see SaveSubtitles).
        /// </summary>
        public void SaveFinalized(string reviewId, TimingEditorFinalized[] items)
        {
            var byKey = new Dictionary<string, TimingEditorFinalized>();
            foreach (var x in items ?? Array.Empty<TimingEditorFinalized>())
                if (x?.K != null)
                    byKey[x.K] = x;
            ChangeReview(reviewId, jobState =>
            {
                foreach (var item in jobState.Items)
                {
                    if (byKey.TryGetValue(KeyOf(item.StartTime, item.EndTime), out var x))
                    {
                        item.Finalized = x.F;
                    }
                }
                return true;
            });
        }

        public string SaveSubtitles(string reviewId, string stamp, bool force, TimingEditorSubtitle[] subtitles, TimingEditorSpeaker[] speakers)
        {
            if (subtitles == null)
                throw new TimingEditorException("No subtitles received.");
            if (speakers == null)
                throw new TimingEditorException("No list of speakers received.");
            foreach (var speaker in speakers)
            {
                var error = TranscriberInteractifReview.GetSpeakerNameError(speaker?.Name)
                    ?? (string.IsNullOrWhiteSpace(speaker.Alias) ? null : TranscriberInteractifReview.GetSpeakerNameError(speaker.Alias))
                    ?? GetSpeakerImageError(speaker.Name, speaker.Image);
                if (error != null)
                    throw new TimingEditorException(error);
            }
            foreach (var sub in subtitles)
            {
                if (sub.S < 0 || sub.E <= sub.S)
                    throw new TimingEditorException($"Invalid timing: {TimeSpan.FromMilliseconds(sub.S):hh\\:mm\\:ss\\.fff} to {TimeSpan.FromMilliseconds(sub.E):hh\\:mm\\:ss\\.fff}.");
                // (a line break in a line: they are separate lines in the file. A subtitle without text, or with empty lines,
                // is read back the same way, see SubtitleFile.FromSrtFile)
                sub.Lines = TranscriberInteractifReview.SplitLines(sub.Lines);
            }

            return ChangeReview(reviewId, jobState =>
            {
                if (!force && StampOf(jobState) != stamp)
                    throw new TimingEditorConflictException("The review was saved from somewhere else (another page?) since it was loaded in this page.");

                // (finalized or not comes with the subtitles: a finalized subtitle stays finalized when it's moved;
                // not sent, ex. an older page: what was saved for the same timing)
                var saved = new Dictionary<string, TranscriberInteractifReview.ReviewItem>();
                foreach (var item in jobState.Items)
                    saved[KeyOf(item.StartTime, item.EndTime)] = item;
                jobState.Items = subtitles
                    .OrderBy(s => s.S)
                    .Select(s =>
                    {
                        var start = TimeSpan.FromMilliseconds(s.S);
                        var end = TimeSpan.FromMilliseconds(s.E);
                        saved.TryGetValue(KeyOf(start, end), out var before);
                        return new TranscriberInteractifReview.ReviewItem
                        {
                            StartTime = start,
                            EndTime = end,
                            Lines = s.Lines,
                            Finalized = s.F ?? before?.Finalized ?? false,
                            SpeakerConfirmed = s.Sc ?? before?.SpeakerConfirmed ?? false
                        };
                    })
                    .ToList();
                jobState.Speakers = speakers
                    .GroupBy(sp => sp.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new TranscriberInteractifReview.ReviewSpeaker
                    {
                        Name = g.Key,
                        Alias = string.IsNullOrWhiteSpace(g.First().Alias) ? null : g.First().Alias.Trim(),
                        Image = string.IsNullOrEmpty(g.First().Image) ? null : g.First().Image
                    })
                    .ToList();
                jobState.Version++;
                return StampOf(jobState);
            });
        }

        /// <summary>The image of a speaker (a part of a frame, made by the page): a JPEG / PNG data URL, not too big (it's in the .wipsubtitles).</summary>
        private const int MaxSpeakerImageLength = 1024 * 1024;
        private static string GetSpeakerImageError(string name, string image)
        {
            if (string.IsNullOrEmpty(image))
                return null;
            if (!image.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal) && !image.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
                return $"The image of the speaker '{name}' is not a JPEG / PNG image.";
            if (image.Length > MaxSpeakerImageLength)
                return $"The image of the speaker '{name}' is too big ({image.Length / 1024} KB, at most {MaxSpeakerImageLength / 1024} KB).";
            return null;
        }

        // ---- The audio ----

        /// <summary>
        /// The settings of the terrain: the ones of the preparer that uses this audio for its terrain,
        /// otherwise the default settings of <see cref="AudioTerrainMapping"/> (said in the answer: 'settingsFrom').
        /// </summary>
        private (float? valleyReach, string from) GetTerrainSettings(string audioId)
        {
            var worker = GetTerrainWorkers().FirstOrDefault(w => w.RefineTiming.SourceAudioIdForTerrain == audioId);
            return worker != null
                ? (worker.RefineTiming.ValleyReach, worker.DisplayName)
                : ((float?)null, null);
        }

        public object GetTerrain(string audioId)
        {
            var pcm = GetPcmAudio(audioId);
            var (valleyReach, from) = GetTerrainSettings(audioId);
            var key = $"{audioId}|{valleyReach}";

            Task<AudioTerrainMapping> task;
            lock (r_terrainLock)
            {
                // (a new audio, ex. the scene was loaded again: computed again)
                if (!r_terrains.TryGetValue(key, out task) || (task.IsFaulted))
                {
                    task = Task.Run(() =>
                    {
                        var terrain = new AudioTerrainMapping(pcm);
                        if (valleyReach != null)
                            terrain.ValleyReach = valleyReach.Value;
                        terrain.Compute();
                        return terrain;
                    });
                    r_terrains[key] = task;
                }
            }

            // (it can take a while: the page asks again until it's done)
            try
            {
                if (!task.Wait(TimeSpan.FromSeconds(1)))
                    return new { computing = true };
            }
            catch (AggregateException ex)
            {
                throw new TimingEditorException($"Can't map the terrain of '{audioId}': {ex.InnerException?.Message ?? ex.Message}");
            }

            var result = task.Result;
            return new
            {
                computing = false,
                settingsFrom = from,
                // [start ms, end ms, kind] of what is not silence: 'S' speech, 'B' breath, 'C' click
                segments = result.DetailedSegments
                    .Where(s => !s.IsSilence)
                    .Select(s => new object[]
                    {
                        (long)s.StartTime.TotalMilliseconds,
                        (long)s.EndTime.TotalMilliseconds,
                        s.Kind == AudioTerrainMapping.SegmentKind.Speech ? "S" : s.Kind == AudioTerrainMapping.SegmentKind.Breath ? "B" : "C"
                    }).ToArray()
            };
        }

        public byte[] GetPeaks(string audioId, TimeSpan start, TimeSpan end, int nbBins)
        {
            var pcm = GetPcmAudio(audioId);
            nbBins = Math.Max(1, Math.Min(nbBins, MaxNbBins));
            var data = pcm.Data;
            var nbSamples = data.Length / 2;
            var startSample = (long)Math.Floor((start - pcm.Offset).TotalSeconds * pcm.SamplingRate);
            var endSample = (long)Math.Ceiling((end - pcm.Offset).TotalSeconds * pcm.SamplingRate);
            var samplesPerBin = Math.Max(1.0, (double)(endSample - startSample) / nbBins);

            // (a long range: only some of the samples are read, it's only an overview)
            var stride = Math.Max(1, (int)(samplesPerBin / 4096));

            // (16 bits: the quiet parts keep their shape)
            var result = new byte[nbBins * 4];
            for (var bin = 0; bin < nbBins; bin++)
            {
                var from = Math.Max(0, startSample + (long)(bin * samplesPerBin));
                var to = Math.Min(nbSamples, startSample + (long)((bin + 1) * samplesPerBin));
                short min = 0, max = 0;
                for (var i = from; i < to; i += stride)
                {
                    var sample = (short)(data[i * 2] | (data[i * 2 + 1] << 8));
                    if (sample < min) min = sample;
                    if (sample > max) max = sample;
                }
                result[bin * 4] = (byte)min;
                result[bin * 4 + 1] = (byte)(min >> 8);
                result[bin * 4 + 2] = (byte)max;
                result[bin * 4 + 3] = (byte)(max >> 8);
            }
            return result;
        }

        public (int samplingRate, byte[] pcm) GetAudio(string audioId)
        {
            var pcm = GetPcmAudio(audioId);
            if (pcm.Offset != TimeSpan.Zero)
                throw new TimingEditorException($"The audio '{audioId}' doesn't start at the start of the scene.");
            return (pcm.SamplingRate, pcm.Data);
        }
    }
}
