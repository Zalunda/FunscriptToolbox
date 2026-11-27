using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Progress;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    /// <summary>
    /// A full transcription by a transcription model of Google (ex. 'gemini-3.5-transcribe', see AIEngineAPIGoogle.Transcribe):
    /// verbatim, with the start / end of each word. The audio is sent in chunks of MaxChunkDuration (like TranscriberAudioFullAI:
    /// what starts in the last 10 s of a chunk is done again by the next one). The subtitles are built from the words: a new one
    /// after a pause (MaxGap), after the end of a sentence (。？！?!), when it would be too long (MaxDuration / MaxChars), or when
    /// the speaker changes (Diarization). Their end is moved EndPadding later (the word ends of the model are too early), but not
    /// after the start of the next one.
    /// The answers of the model (the text and the words of each chunk) are kept in the job state (the only place with the words): the subtitles are built again
    /// from them at each run, so, with 'CanBeUpdated', a change of these rules is applied without any new request.
    /// The language: the one of the transcription (SourceLanguage).
    /// </summary>
    public class TranscriberAudioFullAIGoogleTranscribe : TranscriberAudio
    {
        public class JobState
        {
            /// <summary>Where the next chunk starts (the end of the audio: all done).</summary>
            public TimeSpan NextStartTime { get; set; }

            /// <summary>The answers of the model, one by chunk, in order.</summary>
            public List<CachedChunk> Chunks { get; set; } = new List<CachedChunk>();

            /// <summary>
            /// The subtitles that start before this time were made by the previous type of this step (TranscriberAudioFullAI, the same id,
            /// stopped before the end): kept as they are (null: none).
            /// </summary>
            public TimeSpan? PreviousTypeEndTime { get; set; }
        }

        public class CachedChunk
        {
            public TimeSpan StartTime { get; set; }
            public TimeSpan EndTime { get; set; }
            public string Text { get; set; }
            /// <summary>Their times: in the chunk.</summary>
            public AIEngineAPIGoogle.TranscribedWordInfo[] Words { get; set; }
        }

        [JsonProperty(Order = 20, Required = Required.Always)]
        public string MetadataProduced { get; set; }

        /// <summary>At most 30 minutes with the times of the words (a limit of the model: the rest would be lost).</summary>
        [JsonProperty(Order = 21)]
        public TimeSpan MaxChunkDuration { get; set; } = TimeSpan.FromMinutes(25);

        [JsonProperty(Order = 22)]
        public bool KeepTemporaryFiles { get; set; } = false;

        [JsonProperty(Order = 30, Required = Required.Always)]
        public AIEngineAPIGoogle Engine { get; set; }

        [JsonProperty(Order = 31)]
        public AIEngineConfigOverride EngineConfigOverride { get; set; }

        /// <summary>
        /// The model separates the speakers: a new subtitle when the speaker changes. Its numbers ("spk:0", "spk:1"...) can't identify the
        /// characters (ex. women with similar voices: it alternates between 2 numbers for 4 people; not the same numbers from a chunk to the next).
        /// </summary>
        [JsonProperty(Order = 40)]
        public bool Diarization { get; set; } = false;

        /// <summary>With Diarization: the number of the speaker of each subtitle in this metadata (null: not written).</summary>
        [JsonProperty(Order = 41)]
        public string SpeakerMetadata { get; set; }

        /// <summary>
        /// The same line returned twice (two speakers saying it together, or a doubling of the model): one subtitle, from the first start to
        /// the last end, with MultipleSpeakersMetadata = MultipleSpeakersValue when they are from 2 speakers (null: not written).
        /// </summary>
        [JsonProperty(Order = 42)]
        public string MultipleSpeakersMetadata { get; set; }

        [JsonProperty(Order = 43)]
        public string MultipleSpeakersValue { get; set; } = "Multiple";

        /// <summary>Two subtitles with the same text whose starts are at most this far apart: the same line (see MultipleSpeakersMetadata).</summary>
        [JsonProperty(Order = 44)]
        public TimeSpan SameLineMaxStartDifference { get; set; } = TimeSpan.FromSeconds(1.5);

        [JsonProperty(Order = 50)]
        public TimeSpan MaxGap { get; set; } = TimeSpan.FromMilliseconds(600);

        [JsonProperty(Order = 51)]
        public TimeSpan MaxDuration { get; set; } = TimeSpan.FromSeconds(6);

        [JsonProperty(Order = 52)]
        public int MaxChars { get; set; } = 28;

        [JsonProperty(Order = 53)]
        public TimeSpan EndPadding { get; set; } = TimeSpan.FromMilliseconds(250);

        [JsonProperty(Order = 60)]
        public TranscriberAudioFullAI.TranscriptioToIgnorePattern[] TranscriptionToIgnorePatterns { get; set; }

        private const string EndOfSentence = "。？！?!";

        /// <summary>What starts in the last BufferZone of a chunk may be cut by its end: done again by the next chunk.</summary>
        private static readonly TimeSpan BufferZone = TimeSpan.FromSeconds(10);

        protected override string GetMetadataProduced() => this.MetadataProduced;

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (!base.IsPrerequisitesForAudioMet(context, out reason))
            {
                return false;
            }

            reason = null;
            return true;
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Track transcription)
        {
            if (this.MaxChunkDuration > TimeSpan.FromMinutes(30))
            {
                throw new Exception($"'{this.Id}': 'MaxChunkDuration' is {this.MaxChunkDuration}: at most 30 minutes (with the times of the words, the model ignores the rest).");
            }

            var processStartTime = DateTime.Now;
            var fullPcmAudio = base.GetPcmAudio(context);
            var options = new AIEngineAPIGoogle.TranscribeOptions
            {
                LanguageCodes = GetLanguageCodes(transcription.Language),
                Diarization = this.Diarization
            };

            var jobState = transcription.CurrentJobState as JobState;
            if (jobState == null)
            {
                // (the step was a TranscriberAudioFullAI before, with the same id: a transcription it started continues where it stopped,
                //  its subtitles are kept)
                // (subtitles without a job state of the previous type, ex. a run of an older version: kept; finished: nothing to ask,
                //  otherwise: it continues after the last one)
                var previous = transcription.CurrentJobState as TranscriberAudioFullAI.JobState;
                var hasItems = transcription.Items.Count > 0;
                var continueAt = previous?.NextStartTime
                    ?? (!hasItems ? TimeSpan.Zero : transcription.IsFinished ? fullPcmAudio.EndTime : transcription.Items.Max(i => i.EndTime));
                jobState = new JobState
                {
                    NextStartTime = continueAt,
                    PreviousTypeEndTime = hasItems ? (transcription.IsFinished ? fullPcmAudio.EndTime + TimeSpan.FromHours(1) : continueAt) : (TimeSpan?)null
                };
                transcription.CurrentJobState = jobState;
            }
            var previousTypeItems = jobState.PreviousTypeEndTime is TimeSpan previousEnd
                ? transcription.Items.Where(i => i.StartTime < previousEnd).ToList()
                : new List<TimedItemWithMetadata>();

            // Done before (ex. 'CanBeUpdated'): the subtitles are built again from the answers kept, with the rules of now
            if (jobState.NextStartTime >= fullPcmAudio.EndTime)
            {
                context.RunStep("Build the subtitles from the words (answers kept)", _ => RebuildItems(context, transcription, jobState, previousTypeItems));
            }

            var requestNumber = 1;
            var isStopAfterRequested = false;
            while (jobState.NextStartTime < fullPcmAudio.EndTime)
            {
                // (safe point: the previous chunk is saved)
                if (isStopAfterRequested)
                {
                    throw new UserStoppedWorkerException("Stopped after the request, as asked by the user.");
                }
                context.PauseIfClosing();

                var chunkStartTime = jobState.NextStartTime;
                var chunkEndTime = chunkStartTime + this.MaxChunkDuration;
                var chunkTiming = new Timing(chunkStartTime, chunkEndTime < fullPcmAudio.EndTime ? chunkEndTime : fullPcmAudio.EndTime);
                var chunkName = $"chunk #{requestNumber}, {chunkTiming.StartTime:hh\\:mm\\:ss} to {chunkTiming.EndTime:hh\\:mm\\:ss} of {fullPcmAudio.EndTime:hh\\:mm\\:ss}";
                var number = requestNumber++;
                var request = context.RunStep($"Prepare {chunkName}", _ =>
                {
                    var snippet = fullPcmAudio.ExtractSnippet(chunkTiming.StartTime, chunkTiming.EndTime);
                    var audioBytes = snippet.ToWavBytes();
                    var fileName = $"{chunkTiming.StartTime:hh\\-mm\\-ss\\-fff}.wav";
                    if (this.KeepTemporaryFiles)
                        context.CreateVerboseBinaryFile($"{transcription.Id}_{fileName}", audioBytes, processStartTime);
                    var userParts = new AIRequestPartCollection();
                    userParts.Add(new AIRequestPartAudio(AIRequestSection.PrimaryNodes, fileName, audioBytes, snippet.Duration, chunkTiming.StartTime));
                    return new AIRequest(
                        processStartTime,
                        number,
                        this.Id,
                        null,
                        new AIRequestPartCollection(),
                        userParts,
                        this.MetadataProduced,
                        $"{chunkTiming.StartTime:hh\\:mm\\:ss\\.fff} to {chunkTiming.EndTime:hh\\:mm\\:ss\\.fff} out of {context.WIP.TimelineMap.Duration:hh\\:mm\\:ss\\.fff}",
                        chunkTiming.StartTime);
                });
                context.PauseIfClosing();

                AIEngineAPIGoogle.TranscribeResult result;
                using (var requestNode = context.BeginTask("AI request", chunkName))
                {
                    requestNode.SetCancellable(AIEngineAPI.StopNowHint);
                    requestNode.SetPauseAfterSupported();
                    requestNode.SetStopNowWarning(AIEngineAPI.StopNowCostWarning);
                    try
                    {
                        result = this.Engine.Transcribe(context, request, options, this.EngineConfigOverride);
                        isStopAfterRequested = requestNode.IsStopAfterRequested;
                    }
                    catch (UserStoppedWorkerException)
                    {
                        requestNode.SetState(TaskState.Cancelled, "stopped by the user");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        requestNode.Fail(ex.Message);
                        throw;
                    }
                }

                var chunk = new CachedChunk
                {
                    StartTime = chunkTiming.StartTime,
                    EndTime = chunkTiming.EndTime,
                    Text = result.Text,
                    Words = result.Words
                };
                jobState.Chunks.Add(chunk);

                // (diarization: the model can stop giving the speakers in the middle of a chunk (seen on a long chunk with a lot of speech))
                var nbWithoutSpeaker = this.Diarization ? (result.Words ?? Array.Empty<AIEngineAPIGoogle.TranscribedWordInfo>()).Count(w => w.Speaker == null) : 0;
                if (nbWithoutSpeaker > 0)
                {
                    context.WriteInfo($"{chunkName}: {nbWithoutSpeaker} of {result.Words.Length} words without a speaker (diarization lost by the model; a shorter 'MaxChunkDuration' may help).", ConsoleColor.Yellow);
                }

                // Where the next chunk starts: what starts in the buffer zone at the end of this one is done again by the next one
                var chunkItems = BuildItems(context, chunk, logIgnored: false, out _);
                var isLastChunk = chunkEndTime >= fullPcmAudio.EndTime;
                if (result.IncompleteReason != null)
                {
                    // The answer was cut (ex. too many output tokens): the next chunk starts at the last subtitle received (it may be cut too)
                    var last = chunkItems.OrderBy(i => i.StartTime).LastOrDefault();
                    if (last == null || last.StartTime <= chunkStartTime)
                    {
                        jobState.Chunks.Remove(chunk);
                        throw new Exception($"The transcription of {chunkName} was cut ({result.IncompleteReason}) before a subtitle could be kept: reduce 'MaxChunkDuration' (or raise the maximum of output tokens of the engine, 'RequestBodyExtension').");
                    }
                    jobState.NextStartTime = last.StartTime;
                    context.WriteInfo($"Cut answer: kept up to {last.StartTime:hh\\:mm\\:ss\\.fff}, the next chunk starts there.", ConsoleColor.Yellow);
                }
                else if (isLastChunk)
                {
                    jobState.NextStartTime = fullPcmAudio.EndTime;
                }
                else
                {
                    var startOfBufferZone = chunkEndTime - BufferZone;
                    var itemsToKeep = chunkItems.Where(i => i.StartTime < startOfBufferZone).ToList();
                    var itemsToRedo = chunkItems.Where(i => i.StartTime >= startOfBufferZone).ToList();
                    var keptEnd = itemsToKeep.Count > 0 ? itemsToKeep.Max(i => i.EndTime) : chunkStartTime;
                    var nextStartTime = TimeSpanExtensions.Max(startOfBufferZone, keptEnd);
                    if (itemsToRedo.Count > 0)
                    {
                        nextStartTime = TimeSpanExtensions.Min(nextStartTime, itemsToRedo.Min(i => i.StartTime));
                    }
                    jobState.NextStartTime = nextStartTime;
                    context.WriteInfo($"Buffer zone: kept {itemsToKeep.Count}, will redo {itemsToRedo.Count} item(s), next chunk starts at {nextStartTime:hh\\:mm\\:ss\\.fff}.");
                }

                if (result.Cost != null)
                {
                    dynamic customInfos = result.Cost.CustomInfos ?? new ExpandoObject();
                    customInfos.ChunkStartTime = $"{chunkTiming.StartTime:hh\\:mm\\:ss\\.fff}";
                    customInfos.ChunkEndTime = $"{chunkTiming.EndTime:hh\\:mm\\:ss\\.fff}";
                    customInfos.Duration = $"{chunkTiming.Duration:hh\\:mm\\:ss\\.fff}";
                    result.Cost.CustomInfos = customInfos;
                    result.Cost.NbItemsInResponse = chunkItems.Count;
                    transcription.Costs.Add(result.Cost);
                }

                context.RunStep("Build the subtitles from the words", _ => RebuildItems(context, transcription, jobState, previousTypeItems));
                context.WriteInfo($"Current number of subtitles: {transcription.Items.Count}");
                context.WIP.Save();
            }

            transcription.MarkAsFinished();
            context.WIP.Save();
        }

        /// <summary>The language of the transcription, as a BCP-47 code with its main region (ex. "ja" => "ja-JP"); none: detected by the model.</summary>
        private static string[] GetLanguageCodes(Language language)
        {
            if (language == null)
                return Array.Empty<string>();
            try
            {
                return new[] { CultureInfo.CreateSpecificCulture(language.ShortName).Name };
            }
            catch (CultureNotFoundException)
            {
                return new[] { language.ShortName };
            }
        }

        /// <summary>
        /// The subtitles of the transcription, built again from all the chunks kept (with the rules of now): the ones of each chunk that
        /// start before the next chunk (the rest is in the next one), after the subtitles of the previous type of the step, if any.
        /// </summary>
        private void RebuildItems(SubtitleGeneratorContext context, Track transcription, JobState jobState, List<TimedItemWithMetadata> previousTypeItems)
        {
            var built = new List<(TimedItemWithMetadata Item, string Speaker)>();
            for (var index = 0; index < jobState.Chunks.Count; index++)
            {
                var chunk = jobState.Chunks[index];
                // (the last chunk, not the end of the audio: its buffer zone is in the next chunk, not asked yet)
                TimeSpan? limit = index + 1 < jobState.Chunks.Count
                    ? jobState.Chunks[index + 1].StartTime
                    : jobState.NextStartTime < chunk.EndTime ? jobState.NextStartTime : (TimeSpan?)null;
                var chunkItems = BuildItems(context, chunk, logIgnored: true, out var speakers);
                for (var k = 0; k < chunkItems.Count; k++)
                {
                    if (limit == null || chunkItems[k].StartTime < limit)
                        built.Add((chunkItems[k], speakers[k]));
                }
            }

            // In the order of the time (with several speakers, the model gives the words speaker by speaker, not in the order of the time)
            built = built.OrderBy(b => b.Item.StartTime).ToList();

            // The same line twice (the same text, starts close): one subtitle; from 2 speakers: MultipleSpeakersMetadata
            var merged = new List<TimedItemWithMetadata>();
            var used = new bool[built.Count];
            static string Normalize(string text) => new string((text ?? "").Where(char.IsLetterOrDigit).ToArray());
            for (var i = 0; i < built.Count; i++)
            {
                if (used[i])
                    continue;
                var (item, speaker) = built[i];
                var text = Normalize(item.Metadata.Get(this.MetadataProduced));
                var start = item.StartTime;
                var end = item.EndTime;
                var isMultiple = false;
                for (var j = i + 1; j < built.Count && built[j].Item.StartTime - item.StartTime <= this.SameLineMaxStartDifference; j++)
                {
                    if (used[j] || text.Length == 0 || Normalize(built[j].Item.Metadata.Get(this.MetadataProduced)) != text)
                        continue;
                    used[j] = true;
                    end = TimeSpanExtensions.Max(end, built[j].Item.EndTime);
                    isMultiple |= built[j].Speaker != speaker;
                }
                if (end != item.EndTime || isMultiple)
                {
                    var metadata = new MetadataCollection(item.Metadata);
                    if (isMultiple && !string.IsNullOrWhiteSpace(this.MultipleSpeakersMetadata))
                        metadata[this.MultipleSpeakersMetadata] = this.MultipleSpeakersValue;
                    item = new TimedItemWithMetadata(start, end, metadata);
                }
                merged.Add(item);
            }

            var items = new List<TimedItemWithMetadata>(previousTypeItems);
            items.AddRange(merged);

            // The end: EndPadding later (not after the start of the next one; a subtitle of at least 100 ms). Not the subtitles of the previous type.
            for (var i = previousTypeItems.Count; i < items.Count; i++)
            {
                var item = items[i];
                var end = item.EndTime + this.EndPadding;
                if (i + 1 < items.Count && end > items[i + 1].StartTime)
                    end = TimeSpanExtensions.Max(item.EndTime, items[i + 1].StartTime);
                if (end - item.StartTime < TimeSpan.FromMilliseconds(100))
                    end = item.StartTime + TimeSpan.FromMilliseconds(100);
                if (end != item.EndTime)
                    items[i] = new TimedItemWithMetadata(item.StartTime, end, item.Metadata);
            }

            transcription.Items.Clear();
            transcription.Items.AddRange(items);
        }

        /// <summary>A word that lasts longer than this: a broken time (seen at the start of some chunks), ignored.</summary>
        private static readonly TimeSpan MaxWordDuration = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The words with impossible times (seen at the start of some chunks): the end before the start, longer than MaxWordDuration, or
        /// a start far after the words that follow it in the text (more than 30 s later than the earliest of the next 20 words).
        /// </summary>
        private static List<AIEngineAPIGoogle.TranscribedWordInfo> RemoveBrokenWords(AIEngineAPIGoogle.TranscribedWordInfo[] words, out int nbBroken)
        {
            var kept = new List<AIEngineAPIGoogle.TranscribedWordInfo>();
            for (var i = 0; i < words.Length; i++)
            {
                var word = words[i];
                if (word.EndTime < word.StartTime || word.EndTime - word.StartTime > MaxWordDuration)
                    continue;
                var nextStarts = words.Skip(i + 1).Take(20).Select(w => w.StartTime).ToArray();
                if (nextStarts.Length > 0 && word.StartTime - nextStarts.Min() > TimeSpan.FromSeconds(30))
                    continue;
                kept.Add(word);
            }
            nbBroken = words.Length - kept.Count;
            return kept;
        }

        /// <summary>The subtitles of a chunk, from its words (times: in the chunk => in the scene), without the end padding.</summary>
        private List<TimedItemWithMetadata> BuildItems(SubtitleGeneratorContext context, CachedChunk chunk, bool logIgnored, out List<string> speakers)
        {
            var textBytes = Encoding.UTF8.GetBytes(chunk.Text ?? string.Empty);
            var groups = new List<List<AIEngineAPIGoogle.TranscribedWordInfo>>();
            List<AIEngineAPIGoogle.TranscribedWordInfo> current = null;
            var words = RemoveBrokenWords(chunk.Words ?? Array.Empty<AIEngineAPIGoogle.TranscribedWordInfo>(), out var nbBroken);
            if (nbBroken > 0 && logIgnored)
                context.WriteLog($"Chunk {chunk.StartTime:hh\\:mm\\:ss}: {nbBroken} word(s) with impossible times ignored.");
            foreach (var word in words)
            {
                if (current != null)
                {
                    var previous = current[^1];
                    var tooLong = word.EndTime - current[0].StartTime > this.MaxDuration
                        || current.Sum(w => (w.Text ?? "").Length) + (word.Text ?? "").Length > this.MaxChars;
                    var previousText = (previous.Text ?? "").Trim();
                    var endOfSentence = previousText.Length > 0 && EndOfSentence.Contains(previousText[^1]);
                    var otherSpeaker = this.Diarization && word.Speaker != previous.Speaker;
                    if (word.StartTime - previous.EndTime > this.MaxGap || endOfSentence || tooLong || otherSpeaker)
                        current = null;
                }
                if (current == null)
                {
                    current = new List<AIEngineAPIGoogle.TranscribedWordInfo>();
                    groups.Add(current);
                }
                current.Add(word);
            }

            var items = new List<TimedItemWithMetadata>();
            speakers = new List<string>();
            foreach (var group in groups)
            {
                // (the text between the first and the last word, as in the text of the answer: with its spaces / punctuation)
                var first = group[0];
                var last = group[^1];
                var text = first.StartIndex != null && last.EndIndex != null && last.EndIndex <= textBytes.Length && first.StartIndex < last.EndIndex
                    ? Encoding.UTF8.GetString(textBytes, first.StartIndex.Value, last.EndIndex.Value - first.StartIndex.Value).Trim()
                    : string.Concat(group.Select(w => w.Text)).Trim();
                if (text.Length == 0)
                    continue;

                var startTime = chunk.StartTime + first.StartTime;
                var pattern = this.TranscriptionToIgnorePatterns?.FirstOrDefault(p => p.Regex.IsMatch(text));
                if (pattern != null)
                {
                    if (logIgnored)
                        context.WriteLog($"Ignoring '{pattern.Name}' transcription at [{context.WIP.TimelineMap.ConvertToPartSpecificFileIndexAndTime(startTime)}]: {text}");
                    continue;
                }

                var metadata = new MetadataCollection();
                metadata[this.MetadataProduced] = text;
                if (this.Diarization && first.Speaker != null && !string.IsNullOrWhiteSpace(this.SpeakerMetadata))
                    metadata[this.SpeakerMetadata] = first.Speaker;
                speakers.Add(first.Speaker);
                items.Add(new TimedItemWithMetadata(
                    startTime,
                    chunk.StartTime + last.EndTime,
                    metadata));
            }
            return items;
        }
    }
}
