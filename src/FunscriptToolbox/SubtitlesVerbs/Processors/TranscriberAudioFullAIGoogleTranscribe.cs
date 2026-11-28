using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Progress;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;

namespace FunscriptToolbox.SubtitlesVerbs.Processors
{
    /// <summary>
    /// The engine: AIEngineAPIGoogle.Transcribe. The answers of the model (the text and the words of each request) are kept in the job state
    /// (the only place with the words): the subtitles are built again from them at each run.
    /// What the model does (probes of 2026-11-28, see the notes of AIEngineAPIGoogle.Transcribe.cs): it can stop writing after a long pause
    /// (status 'completed', the same audio again = the same stop, a request starting at its last word goes on), the first ~10 s of a request
    /// are less reliable, and two requests over the same audio don't always hear the same lines.
    /// </summary>
    [ConfigDoc("A transcription of the whole audio by a transcription model of Google (ex. 'gemini-3.5-transcribe'): verbatim, with the start / end of each word, " +
        "in the language of the video. The audio is sent in chunks (MaxChunkDuration, BufferZone shared by two chunks), in one or several main passes shifted by a " +
        "few ms (MainPassCount: another reading of the same audio); when the model stops writing before the end " +
        "of a chunk (it can stop after a long pause), the next chunk starts at its last word (EarlyStopMinDuration). Then, in short requests sent in parallel " +
        "(MaxParallelRequests): the places where a chunk hands over to the next one (BoundaryRetryMargin) and the gaps between the subtitles (RetryGapMinDuration). " +
        "Each line is taken from the request where it's farthest from the start / end of the audio sent (the first seconds of a request are less reliable, its " +
        "last line can be cut). The subtitles are built from the words: a new one after a pause (MaxGap), after the end of a sentence (。？！?!), when it would be " +
        "too long (MaxDuration), or when the speaker changes (Diarization); their end is moved EndPadding later (the word ends of the model are too early), not " +
        "after the start of the next one.",
        Advice = "With CanBeUpdated, a change of these rules is applied at the next run without any new request (the answers of the model are kept), except the " +
        "rules that decide what is asked (the chunks, the retries): what wasn't asked yet is asked.")]
    public partial class TranscriberAudioFullAIGoogleTranscribe : TranscriberAudio
    {
        public class JobState
        {
            /// <summary>All the answers received (the chunks and the retries), found again by their exact times (StartTime, EndTime).</summary>
            public List<CachedChunk> Chunks { get; set; } = new List<CachedChunk>();

            /// <summary>
            /// The gaps already asked again (their audio: a chunk of Chunks), kept so that the next runs send the same requests (from the cache)
            /// and don't ask them again, even if the rules that find the gaps change.
            /// </summary>
            public List<GapRetry> GapRetries { get; set; } = new List<GapRetry>();

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
            /// <summary>Non-null when the model returned status 'incomplete' (cut answer).</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string IncompleteReason { get; set; }
        }

        public class GapRetry
        {
            /// <summary>The gap asked again (times of the scene).</summary>
            public TimeSpan HoleStart { get; set; }
            public TimeSpan HoleEnd { get; set; }
            /// <summary>The audio sent for it (its answer: the chunk of the job state with these times).</summary>
            public TimeSpan WindowStart { get; set; }
            public TimeSpan WindowEnd { get; set; }
        }

        [ConfigDoc("The metadata the text goes in (ex. \"VoiceText\").")]
        [JsonProperty(Order = 20, Required = Required.Always)]
        public string MetadataProduced { get; set; }

        [ConfigDoc("The duration of the audio sent in each request of the main pass. At most 30 minutes with the times of the words (a limit of the model: the rest would be lost).",
            Advice = "5 minutes: the long chunks lose more (ex. the speakers stop being given after a few minutes).")]
        [JsonProperty(Order = 21)]
        public TimeSpan MaxChunkDuration { get; set; } = TimeSpan.FromMinutes(5);

        [ConfigDoc("The number of main passes over the whole audio. Pass k of n: its chunks are shifted by k / n of a chunk (its first chunk is k / n of " +
            "MaxChunkDuration: with 5-minute chunks and 2 passes, the chunks of pass 2 start at 0, ~2.5, ~7.5 minutes...) and by k / n of an audio token of the " +
            "model (40 ms, 25 tokens / s: 2 passes = 0 and 20 ms; 3 = 0, 13.33 and 26.67 ms...). Another reading of the same audio (the same request again gives " +
            "the same answer, the same stops), and the start / end of the chunks of a pass (less reliable) are in the middle of the chunks of the others: a line " +
            "missed by one pass can be heard by another. Each pass has its own chunks, early stops and boundary retries; the gap retries come after all of them. " +
            "MaxTotalAudioFactor must leave room for them.",
            Advice = "2: much fewer lines missed (MDVR-093: a chunk that stopped after 1 minute heard everything shifted by 20 ms), for one more pass of the cost.")]
        [JsonProperty(Order = 22)]
        public int MainPassCount { get; set; } = 3;

        [ConfigDoc("The audio shared by two chunks of the main pass: what starts in the last BufferZone of a chunk is also in the next one (each line is then taken " +
            "from the chunk where it's farthest from the start / end of the audio sent).")]
        [JsonProperty(Order = 23)]
        public TimeSpan BufferZone { get; set; } = TimeSpan.FromSeconds(10);

        [ConfigDoc("When the last word of a chunk ends at least this long before the end of the audio sent (the model can stop writing after a long pause, and the same " +
            "audio again gives the same stop), the next chunk starts at the end of that word, instead of near the end of the chunk. Only when the chunk got at least " +
            "this long past its start (otherwise: the next chunk as usual, the gap is left to the gap retries). null: disabled.")]
        [JsonProperty(Order = 24)]
        public TimeSpan? EarlyStopMinDuration { get; set; } = TimeSpan.FromSeconds(15);

        [ConfigDoc("Each place where a chunk of the main pass hands over to the next one is asked again in a request centered on it, with this much audio on each " +
            "side (the line said there is at the start of the next chunk, where the model is less reliable, and can be cut by the end of the previous one). " +
            "Not done where a chunk starts at an early stop (EarlyStopMinDuration). null: not done.")]
        [JsonProperty(Order = 25)]
        public TimeSpan? BoundaryRetryMargin { get; set; } = null;

        [ConfigDoc("A gap between the subtitles (the ones matching TranscriptionToIgnorePatterns not counted) of at least this duration is asked again, in short " +
            "requests (RetryPreRoll, RetryPostRoll, RetryWindowMaxDuration). A gap already asked is not asked again. null: disabled.",
            Advice = "One speaker: ~10 s. Several speakers: 2 to 3 s (the lines of the other speakers are often missed, even in short gaps).")]
        [JsonProperty(Order = 26)]
        public TimeSpan? RetryGapMinDuration { get; set; } = TimeSpan.FromSeconds(10);

        [ConfigDoc("A gap retry: the audio sent before the gap (the first seconds of a request are less reliable).")]
        [JsonProperty(Order = 27)]
        public TimeSpan RetryPreRoll { get; set; } = TimeSpan.FromSeconds(10);

        [ConfigDoc("A gap retry: the audio sent after the gap.")]
        [JsonProperty(Order = 28)]
        public TimeSpan RetryPostRoll { get; set; } = TimeSpan.FromSeconds(5);

        [ConfigDoc("A gap retry: at most this much audio in one request (with RetryPreRoll and RetryPostRoll). A longer gap is asked in several requests, close gaps " +
            "together in one.",
            Advice = "Keep it short: a long request can stop again at a pause, 30-60 s requests heard 60 of the 64 lines of a 3.5-minute blackout (MDVR-093).")]
        [JsonProperty(Order = 29)]
        public TimeSpan RetryWindowMaxDuration { get; set; } = TimeSpan.FromSeconds(60);

        [ConfigDoc("The retries (BoundaryRetryMargin, RetryGapMinDuration) are sent this many at a time. The chunks of the main pass: one at a time (each one starts " +
            "where the previous one stopped). The engine can still wait between two requests (its TranscribeMinTimeBetweenRequests).")]
        [JsonProperty(Order = 30)]
        public int MaxParallelRequests { get; set; } = 4;

        [ConfigDoc("Hard cap on total audio duration to transcribe (the main passes + all the retries combined) as a multiple of the video duration (e.g. 3.0 = at " +
            "most 3x video length, must be >= 1.0, and more than MainPassCount: each main pass is ~1x).")]
        [JsonProperty(Order = 31)]
        public double MaxTotalAudioFactor { get; set; } = 3.0;

        [ConfigDoc("Keeps the audio chunks sent to the model in the backup folder of the scene.")]
        [JsonProperty(Order = 32)]
        public bool KeepTemporaryFiles { get; set; } = false;

        [ConfigDoc("The Google engine (AIEngineAPIGoogle) with a transcription model (ex. \"gemini-3.5-transcribe\"; UseBatchMode false: the transcription models have no batch mode).")]
        [JsonProperty(Order = 35, Required = Required.Always)]
        public AIEngineAPIGoogle Engine { get; set; }

        [ConfigDoc("The model separates the speakers: a new subtitle when the speaker changes. Its numbers (\"spk:0\", \"spk:1\"...) can't identify the " +
            "characters (ex. women with similar voices: it alternates between 2 numbers for 4 people; not the same numbers from a chunk to the next).")]
        [JsonProperty(Order = 40)]
        public bool Diarization { get; set; } = false;

        [ConfigDoc("With Diarization: the number of the speaker of each subtitle in this metadata (null: not written).")]
        [JsonProperty(Order = 41)]
        public string SpeakerMetadata { get; set; }

        [ConfigDoc("The same line returned twice (two speakers saying it together, or a doubling of the model): one subtitle, from the first start to " +
            "the last end, with MultipleSpeakersMetadata = MultipleSpeakersValue when they are from 2 speakers (null: not written).")]
        [JsonProperty(Order = 42)]
        public string MultipleSpeakersMetadata { get; set; }

        [ConfigDoc("The value written in MultipleSpeakersMetadata for a line said by 2 speakers.")]
        [JsonProperty(Order = 43)]
        public string MultipleSpeakersValue { get; set; } = "Multiple";

        [ConfigDoc("Two subtitles with the same text whose starts are at most this far apart: the same line (see MultipleSpeakersMetadata).")]
        [JsonProperty(Order = 44)]
        public TimeSpan SameLineMaxStartDifference { get; set; } = TimeSpan.FromSeconds(1.5);

        [ConfigDoc("The answers of the requests are cut into phrases (the words without a pause of more than this between them, of the same speaker with Diarization); " +
            "each phrase is taken from the request where it's farthest from the start / end of the audio sent, a phrase of another request at the same time is " +
            "left out. A phrase that matches TranscriptionToIgnorePatterns is left out first. The subtitles are then built from the words of the phrases kept " +
            "(MaxGap, EndOfSentenceMinGap, MaxDuration).",
            Advice = "Short: a long phrase can hide a short line that only another request heard.")]
        [JsonProperty(Order = 49)]
        public TimeSpan PhraseMaxGap { get; set; } = TimeSpan.FromMilliseconds(300);

        [ConfigDoc("Splits into a new subtitle when the pause between two words exceeds this duration (even without sentence-ending punctuation). null: disabled (no split on gap alone).")]
        [JsonProperty(Order = 50)]
        public TimeSpan? MaxGap { get; set; } = TimeSpan.FromMilliseconds(600);

        [ConfigDoc("Splits after sentence-ending punctuation (。？！?!) only when the pause to the next word is at least this duration. 00:00:00: always split; null: never split on punctuation.")]
        [JsonProperty(Order = 51)]
        public TimeSpan? EndOfSentenceMinGap { get; set; } = TimeSpan.Zero;

        [ConfigDoc("Splits into a new subtitle when adding the next word would exceed this duration. null: disabled.")]
        [JsonProperty(Order = 52)]
        public TimeSpan? MaxDuration { get; set; } = TimeSpan.FromSeconds(6);

        [ConfigDoc("The end of each subtitle is moved this much later (the word ends of the model are too early), but not after the start of the next one.")]
        [JsonProperty(Order = 54)]
        public TimeSpan EndPadding { get; set; } = TimeSpan.FromMilliseconds(250);

        [ConfigDoc("A phrase (see PhraseMaxGap) or a subtitle whose whole text matches one of these patterns is left out (ex. moans, a phrase the model writes on " +
            "silences). Each pattern: { \"Name\", \"Pattern\" }.")]
        [JsonProperty(Order = 60)]
        public TranscriberAudioFullAI.TranscriptioToIgnorePattern[] TranscriptionToIgnorePatterns { get; set; }

        private const string EndOfSentence = "。？！?!";

        /// <summary>
        /// An answer that ends with the same word this many times in a row (or with a word of the same character this many times, ex. 'おおおお...')
        /// : the model repeated itself until its limit (seen in 'incomplete' answers), the end is cut where the repetition starts.
        /// Only at the end of the answer: a real series of moans in the middle is kept.
        /// </summary>
        private const int MinRepetitionsToCut = 10;

        /// <summary>The text of the answer of a request (UTF-8, the indexes of its words are in it).</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CachedChunk, byte[]> rs_textBytes = new System.Runtime.CompilerServices.ConditionalWeakTable<CachedChunk, byte[]>();

        /// <summary>Two subtitles overlap when they are closer than this (also: the margin of a line of another request inside a longer one).</summary>
        private static readonly TimeSpan OverlapRecoveryMargin = TimeSpan.FromMilliseconds(300);

        /// <summary>A word that lasts longer than this: a broken time (seen at the start of some chunks), ignored.</summary>
        private static readonly TimeSpan MaxWordDuration = TimeSpan.FromSeconds(5);

        /// <summary>
        /// An answer with more words than this by second of audio: the model repeated itself (seen: 6,984 words for 2.5 minutes), ignored.
        /// (the speech of the scenes seen: at most ~2 words / s)
        /// </summary>
        private const double LoopingMinWordsPerSecond = 8.0;

        /// <summary>
        /// A line of a request that overlaps lines of other requests (more reliable there) replaces them when they are all inside it and leave at
        /// least this much of its speech (the times of its words, and at least half of them) without a line: the other request cut the speech in
        /// shorter lines and missed some.
        /// </summary>
        private static readonly TimeSpan MinUncoveredForLongerLine = TimeSpan.FromSeconds(1);

        /// <summary>The gap retries: at most this number of rounds in a run (the gaps left by a round, not asked yet, are asked in the next one).</summary>
        private const int MaxGapRetryRounds = 2;

        /// <summary>The audio of a request is read by the model in pieces of this length from its start (25 audio tokens / s): the main passes are shifted by a fraction of it.</summary>
        private static readonly TimeSpan AudioTokenDuration = TimeSpan.FromMilliseconds(40);

        /// <summary>
        /// The retries start this much later than computed, to be on another grid of audio tokens than all the requests of the main passes.
        /// The starts of the chunks of a pass are its shift + a multiple of 100 ms (the times of the words, BufferZone), so its chunks are at 2 phases
        /// of a token (100 ms = 2.5 tokens: + 0 or + 20 ms). With n passes (shifts of 40 ms x k / n), the phases used are the multiples of
        /// (20 ms / n) x gcd(2, n): the retries are shifted by half of that (1 or 2 passes: 10 ms; 3: 3.33 ms; 4: 5 ms; 5: 2 ms).
        /// (the probes: a shift of a whole token, on the same grid, also changed the answer; this is only to be sure)
        /// </summary>
        private TimeSpan RetryGridShift => TimeSpan.FromTicks(TimeSpan.FromMilliseconds(10).Ticks * (this.MainPassCount % 2 == 0 ? 2 : 1) / this.MainPassCount);

        protected override string GetMetadataProduced() => this.MetadataProduced;

        private enum ReadingKind
        {
            Chunk,
            Boundary,
            Gap
        }

        /// <summary>An answer used to build the subtitles: a chunk of the main pass or a retry.</summary>
        private sealed class Reading
        {
            public Reading(CachedChunk chunk, ReadingKind kind)
            {
                this.Chunk = chunk;
                this.Kind = kind;
            }

            public CachedChunk Chunk { get; }
            public ReadingKind Kind { get; }
        }

        /// <summary>A request to do (or found in the job state).</summary>
        private sealed class RequestToDo
        {
            public RequestToDo(Timing timing, string name, ReadingKind kind)
            {
                this.Timing = timing;
                this.Name = name;
                this.Kind = kind;
            }

            public Timing Timing { get; }
            public string Name { get; }
            public ReadingKind Kind { get; }
            /// <summary>The answer (null: not sent, ex. the budget).</summary>
            public CachedChunk Result { get; set; }
            // (for the run report)
            public int? Pass { get; set; }
            public int? Round { get; set; }
            public TimeSpan? HoleStart { get; set; }
            public TimeSpan? HoleEnd { get; set; }
            public string NotSent { get; set; }
            public double? Seconds { get; set; }
            public string Note { get; set; }
            public bool FromCache { get; set; }
            public Cost Cost { get; set; }
        }

        /// <summary>A phrase of an answer: its words without a pause of more than PhraseMaxGap between them (see PhraseMaxGap).</summary>
        private sealed class Phrase
        {
            public Phrase(CachedChunk chunk, List<AIEngineAPIGoogle.TranscribedWordInfo> words, TimedItemWithMetadata item)
            {
                this.Chunk = chunk;
                this.Words = words;
                this.Item = item;
            }

            public CachedChunk Chunk { get; }
            /// <summary>Their times: in the chunk.</summary>
            public List<AIEngineAPIGoogle.TranscribedWordInfo> Words { get; }
            /// <summary>Its times in the scene, its text.</summary>
            public TimedItemWithMetadata Item { get; }
            public string Speaker => this.Words[0].Speaker;
        }

        private sealed class Candidate
        {
            public Candidate(Phrase phrase, int readingIndex, ReadingKind kind, double score)
            {
                this.Phrase = phrase;
                this.Item = phrase.Item;
                this.Speaker = phrase.Speaker;
                // (at least 100 ms each: the times of the model are in steps of 0.1 s)
                this.WordSpans = phrase.Words
                    .Select(w => (phrase.Chunk.StartTime + w.StartTime, phrase.Chunk.StartTime + TimeSpanExtensions.Max(w.EndTime, w.StartTime + TimeSpan.FromMilliseconds(100))))
                    .ToList();
                this.ReadingIndex = readingIndex;
                this.Kind = kind;
                this.Score = score;
            }

            public Phrase Phrase { get; }
            public TimedItemWithMetadata Item { get; }
            public string Speaker { get; }
            /// <summary>The times of its words (in the scene): where there is speech in the phrase (not its pauses).</summary>
            public List<(TimeSpan Start, TimeSpan End)> WordSpans { get; }
            public int ReadingIndex { get; }
            public ReadingKind Kind { get; }
            /// <summary>How far (in seconds) the line is from the start / end of the audio sent (the closest of the two): the more, the more reliable.</summary>
            public double Score { get; }
        }

        private void ValidateSettings()
        {
            if (this.MaxChunkDuration > TimeSpan.FromMinutes(30))
            {
                throw new Exception($"'{this.Id}': 'MaxChunkDuration' is {this.MaxChunkDuration}: at most 30 minutes (with the times of the words, the model ignores the rest).");
            }
            if (this.BufferZone < TimeSpan.Zero || this.BufferZone >= this.MaxChunkDuration)
            {
                throw new Exception($"'{this.Id}': 'BufferZone' ({this.BufferZone}) must be >= 0 and smaller than 'MaxChunkDuration' ({this.MaxChunkDuration}).");
            }
            if (this.EarlyStopMinDuration is TimeSpan earlyStop && earlyStop <= TimeSpan.Zero)
            {
                throw new Exception($"'{this.Id}': 'EarlyStopMinDuration' ({earlyStop}) must be > 0 (null: disabled).");
            }
            if (this.BoundaryRetryMargin is TimeSpan boundaryMargin && boundaryMargin <= TimeSpan.Zero)
            {
                throw new Exception($"'{this.Id}': 'BoundaryRetryMargin' ({boundaryMargin}) must be > 0 (null: not done).");
            }
            if (this.RetryGapMinDuration is TimeSpan minGap && minGap <= TimeSpan.Zero)
            {
                throw new Exception($"'{this.Id}': 'RetryGapMinDuration' ({minGap}) must be > 0 (null: disabled).");
            }
            if (this.RetryPreRoll < TimeSpan.Zero || this.RetryPostRoll < TimeSpan.Zero)
            {
                throw new Exception($"'{this.Id}': 'RetryPreRoll' ({this.RetryPreRoll}) and 'RetryPostRoll' ({this.RetryPostRoll}) must be >= 0.");
            }
            if (this.RetryWindowMaxDuration <= this.RetryPreRoll + this.RetryPostRoll)
            {
                throw new Exception($"'{this.Id}': 'RetryWindowMaxDuration' ({this.RetryWindowMaxDuration}) must be longer than 'RetryPreRoll' + 'RetryPostRoll' ({this.RetryPreRoll + this.RetryPostRoll}).");
            }
            if (this.MaxParallelRequests < 1)
            {
                throw new Exception($"'{this.Id}': 'MaxParallelRequests' ({this.MaxParallelRequests}) must be >= 1.");
            }
            if (this.MaxTotalAudioFactor < 1.0)
            {
                throw new Exception($"'{this.Id}': 'MaxTotalAudioFactor' ({this.MaxTotalAudioFactor}) must be >= 1.0.");
            }
            if (this.MainPassCount < 1)
            {
                throw new Exception($"'{this.Id}': 'MainPassCount' ({this.MainPassCount}) must be >= 1.");
            }
            if (this.MainPassCount > 1 && this.MaxChunkDuration.Ticks / this.MainPassCount <= 2 * this.BufferZone.Ticks)
            {
                throw new Exception($"'{this.Id}': with 'MainPassCount' {this.MainPassCount}, the first chunk of a pass can be as short as 'MaxChunkDuration' / {this.MainPassCount} ({TimeSpan.FromTicks(this.MaxChunkDuration.Ticks / this.MainPassCount)}): it must be longer than 2 x 'BufferZone' ({this.BufferZone}).");
            }
            if (this.MaxTotalAudioFactor <= this.MainPassCount)
            {
                throw new Exception($"'{this.Id}': 'MaxTotalAudioFactor' ({this.MaxTotalAudioFactor}) must be more than 'MainPassCount' ({this.MainPassCount}): each main pass is ~1x the audio (a bit more with BufferZone), the retries come on top.");
            }
        }

        protected override void DoWorkInternal(SubtitleGeneratorContext context, Track transcription)
        {
            // (debug, for the tests of the retrier: '.Worker.<id>.run.json', with ExportMetadataSrt; also when stopped / failed)
            var report = new RunReport();
            var status = "stopped / failed";
            try
            {
                DoWork(context, transcription, report);
                status = "completed";
            }
            catch (Exception ex)
            {
                status = $"{ex.GetType().Name}: {ex.Message}";
                throw;
            }
            finally
            {
                WriteRunReport(context, transcription, report, status);
            }
        }

        private void DoWork(SubtitleGeneratorContext context, Track transcription, RunReport report)
        {
            ValidateSettings();

            var processStartTime = DateTime.Now;
            var fullPcmAudio = base.GetPcmAudio(context);
            var audioEnd = fullPcmAudio.EndTime;
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
                jobState = new JobState
                {
                    PreviousTypeEndTime = transcription.Items.Count == 0 ? (TimeSpan?)null
                        : transcription.IsFinished ? audioEnd + TimeSpan.FromHours(1)
                        : previous?.NextStartTime ?? transcription.Items.Max(i => i.EndTime)
                };
                transcription.CurrentJobState = jobState;
            }
            jobState.Chunks ??= new List<CachedChunk>();
            jobState.GapRetries ??= new List<GapRetry>();
            var previousTypeItems = jobState.PreviousTypeEndTime is TimeSpan previousEnd
                ? transcription.Items.Where(i => i.StartTime < previousEnd).ToList()
                : new List<TimedItemWithMetadata>();
            // (the subtitles of this type: after the ones of the previous type)
            var searchStart = jobState.PreviousTypeEndTime ?? TimeSpan.Zero;
            report.AudioEnd = audioEnd;
            report.SearchStart = searchStart;
            report.JobState = jobState;

            var maxAudioBudget = TimeSpan.FromTicks((long)(audioEnd.Ticks * this.MaxTotalAudioFactor));
            var requestNumber = 1;
            var isStopAfterRequested = false;
            // (the answers used by this run: the audio of the ones asked counts in MaxTotalAudioFactor)
            var usedChunks = new HashSet<CachedChunk>();
            var readings = new List<Reading>();
            // (the chunks of each main pass, in their order)
            var passes = new List<List<CachedChunk>>();
            // (the chunks that start at an early stop of the previous one: no boundary retry there)
            var continuationStarts = new HashSet<TimeSpan>();

            // 1. The main passes: chunk after chunk, each one starting where the previous one stopped (the answers of the job state, when they are
            //    there). Each pass starts a fraction of an audio token later than the previous one: another reading of the same audio.
            for (var pass = 0; pass < this.MainPassCount; pass++)
            {
                var mainChunks = new List<CachedChunk>();
                passes.Add(mainChunks);
                var cursor = searchStart + TimeSpan.FromTicks(AudioTokenDuration.Ticks * pass / this.MainPassCount);
                // (every chunk of the pass on its own grid of audio tokens: its shift + a multiple of 40 ms, rounded down, always forward;
                //  a start taken from the times of the words, multiples of 100 ms = 2.5 tokens, would otherwise be on the grid of another pass)
                var passShift = cursor;
                TimeSpan OnPassGrid(TimeSpan time, TimeSpan chunkStart)
                {
                    var snapped = passShift + TimeSpan.FromTicks((time - passShift).Ticks / AudioTokenDuration.Ticks * AudioTokenDuration.Ticks);
                    return snapped > chunkStart ? snapped : snapped + AudioTokenDuration;
                }
                var passName = this.MainPassCount > 1 ? $"pass {pass + 1}/{this.MainPassCount}, " : string.Empty;
                while (cursor < audioEnd)
                {
                    StopOrPauseIfAsked();

                    var chunkStartTime = cursor;
                    // (the first chunk of pass k of n: k / n of a chunk, so that the limits of the chunks of the passes are not at the same places)
                    var chunkEndTime = chunkStartTime + (mainChunks.Count == 0
                        ? TimeSpan.FromTicks(this.MaxChunkDuration.Ticks * (pass == 0 ? this.MainPassCount : pass) / this.MainPassCount)
                        : this.MaxChunkDuration);
                    var chunkTiming = new Timing(chunkStartTime, chunkEndTime < audioEnd ? chunkEndTime : audioEnd);
                    var chunkName = $"{passName}chunk #{mainChunks.Count + 1}, {chunkTiming.StartTime:hh\\:mm\\:ss\\.fff} to {chunkTiming.EndTime:hh\\:mm\\:ss} of {audioEnd:hh\\:mm\\:ss}";
                    var todo = new RequestToDo(chunkTiming, chunkName, ReadingKind.Chunk) { Pass = pass + 1 };
                    if (!RunRequests(new[] { todo }, out var budgetMessage))
                    {
                        throw new Exception($"'{this.Id}': {budgetMessage}.");
                    }
                    var chunk = todo.Result;
                    var fromCache = todo.FromCache;
                    if (fromCache)
                    {
                        context.WriteInfo($"Cache hit for {chunkName} ({chunk.Words?.Length ?? 0} words).");
                    }
                    mainChunks.Add(chunk);
                    readings.Add(new Reading(chunk, ReadingKind.Chunk));

                    var usableWords = GetUsableWords(chunk, out _, out var nbRepeated);
                    var isIgnoredLoop = IsLooping(chunk) || (usableWords.Count == 0 && nbRepeated > 0);

                    // (diarization: the model can stop giving the speakers in the middle of a chunk (seen on a long chunk with a lot of speech))
                    var nbWithoutSpeaker = this.Diarization ? (chunk.Words ?? Array.Empty<AIEngineAPIGoogle.TranscribedWordInfo>()).Count(w => w.Speaker == null) : 0;
                    if (nbWithoutSpeaker > 0 && !fromCache && !isIgnoredLoop)
                    {
                        context.WriteInfo($"{chunkName}: {nbWithoutSpeaker} of {chunk.Words.Length} words without a speaker (diarization lost by the model; a shorter 'MaxChunkDuration' may help).", ConsoleColor.Yellow);
                    }

                    // Where the next chunk starts: from the words themselves (all of them, also the ones of TranscriptionToIgnorePatterns), not from
                    // the phrases / subtitles, so that a change of their rules doesn't change the requests
                    var chunkItems = BuildPhrases(context, chunk, logIgnored: false).Select(p => p.Item).ToList();
                    var wordRuns = GetWordRuns(chunk);
                    var lastWordEnd = GetLastWordEnd(chunk);
                    if (chunk.IncompleteReason != null && !isIgnoredLoop)
                    {
                        // The answer was cut (ex. too many output tokens): the next chunk starts at the last series of words received (it may be cut too)
                        var last = wordRuns.Count == 0 ? null : new Timing(wordRuns[^1].Start, wordRuns[^1].End);
                        if (last == null || last.StartTime <= chunkStartTime)
                        {
                            mainChunks.Remove(chunk);
                            readings.RemoveAll(r => r.Chunk == chunk);
                            jobState.Chunks.Remove(chunk);
                            throw new Exception($"The transcription of {chunkName} was cut ({chunk.IncompleteReason}) before a subtitle could be kept: reduce 'MaxChunkDuration' (or raise the maximum of output tokens of the engine, 'RequestBodyExtension').");
                        }
                        cursor = OnPassGrid(last.StartTime, chunkStartTime);
                        todo.Note = $"cut answer: the next chunk starts at {cursor:hh\\:mm\\:ss\\.fff}";
                        if (!fromCache)
                            context.WriteInfo($"Cut answer: kept up to {last.StartTime:hh\\:mm\\:ss\\.fff}, the next chunk starts there.", ConsoleColor.Yellow);
                    }
                    else if (chunkEndTime >= audioEnd)
                    {
                        cursor = audioEnd;
                        if (isIgnoredLoop)
                            todo.Note = "looping answer ignored (treated as empty)";
                    }
                    else if (this.EarlyStopMinDuration is TimeSpan earlyStop
                        && lastWordEnd is TimeSpan lastEnd
                        && chunkTiming.EndTime - lastEnd >= earlyStop
                        && lastEnd - chunkStartTime >= earlyStop)
                    {
                        // The model stopped writing (ex. after a long pause; the same audio again = the same stop): the next chunk starts at its last word
                        cursor = OnPassGrid(lastEnd, chunkStartTime);
                        continuationStarts.Add(cursor);
                        todo.Note = $"early stop: nothing in the last {chunkTiming.EndTime - lastEnd:hh\\:mm\\:ss\\.f}, the next chunk starts at the last word";
                        if (!fromCache)
                            context.WriteInfo($"Early stop: nothing after {lastEnd:hh\\:mm\\:ss\\.fff} ({chunkTiming.EndTime - lastEnd:hh\\:mm\\:ss} before the end of the chunk), the next chunk starts there.", ConsoleColor.Yellow);
                    }
                    else
                    {
                        // The last BufferZone of this one is also in the next one (from the start of the series of words said there, if any)
                        var startOfBufferZone = chunkEndTime - BufferZone;
                        var crossing = wordRuns.FirstOrDefault(r => r.Start < startOfBufferZone && r.End > startOfBufferZone);
                        var nextStartTime = crossing.End > startOfBufferZone ? crossing.Start : startOfBufferZone;
                        // (always forward)
                        cursor = OnPassGrid(nextStartTime > chunkStartTime ? nextStartTime : startOfBufferZone, chunkStartTime);
                        var nbRunsAlsoInNext = wordRuns.Count(r => r.Start >= cursor);
                        todo.Note = isIgnoredLoop
                            ? $"looping answer ignored (treated as empty): the next chunk starts at {cursor:hh\\:mm\\:ss\\.fff}"
                            : $"the next chunk starts at {cursor:hh\\:mm\\:ss\\.fff} ({nbRunsAlsoInNext} series of words also in it)";
                        if (!fromCache)
                            context.WriteInfo($"Buffer zone: {nbRunsAlsoInNext} series of words also in the next chunk, it starts at {cursor:hh\\:mm\\:ss\\.fff}.");
                    }

                    if (todo.Cost != null)
                    {
                        todo.Cost.NbItemsInResponse = chunkItems.Count;
                    }

                    if (!fromCache)
                    {
                        context.RunStep("Build the subtitles from the words", _ => RebuildItems(context, transcription, readings, previousTypeItems, searchStart, audioEnd, report, isFinal:false));
                        context.WriteInfo($"Current number of subtitles: {transcription.Items.Count}");
                        context.WIP.Save();
                    }
                }
            }

            // (also when chunks were received: the chunks taken from the job state after them must be in the subtitles too)
            var currentItems = context.RunStep("Build the subtitles from the words", _ => RebuildItems(context, transcription, readings, previousTypeItems, searchStart, audioEnd, report, isFinal:false));

            // 2. The boundaries: each place where a chunk hands over to the next one (in each pass), asked again in a request centered on it
            if (this.BoundaryRetryMargin is TimeSpan boundaryMargin)
            {
                var todos = new List<RequestToDo>();
                foreach (var (mainChunks, index) in passes.SelectMany(p => Enumerable.Range(0, Math.Max(0, p.Count - 1)).Select(i => (p, i))))
                {
                    var nextStart = mainChunks[index + 1].StartTime;
                    var end = mainChunks[index].EndTime;
                    if (nextStart >= end || continuationStarts.Contains(nextStart))
                        continue;
                    // (on another grid of audio tokens than the main passes)
                    var timing = new Timing(
                        TimeSpanExtensions.Max(searchStart, nextStart - boundaryMargin) + this.RetryGridShift,
                        TimeSpanExtensions.Min(audioEnd, end + boundaryMargin + this.RetryGridShift));
                    todos.Add(new RequestToDo(
                        timing,
                        $"boundary {end:hh\\:mm\\:ss} (audio {timing.StartTime:hh\\:mm\\:ss\\.f} to {timing.EndTime:hh\\:mm\\:ss\\.f})",
                        ReadingKind.Boundary)
                    { Pass = passes.IndexOf(mainChunks) + 1, Note = $"between the chunks {mainChunks[index].StartTime:hh\\:mm\\:ss\\.fff} and {nextStart:hh\\:mm\\:ss\\.fff}" });
                }
                currentItems = RunRetries(todos, "Boundary retries") ?? currentItems;
            }

            // 3. The gaps between the subtitles: asked again in short requests (the ones of the previous runs first, from the job state)
            if (this.RetryGapMinDuration is TimeSpan retryGapMinDuration)
            {
                jobState.GapRetries.RemoveAll(g => FindCachedChunk(jobState, g.WindowStart, g.WindowEnd) == null);
                var savedWindows = jobState.GapRetries
                    .Select(g => FindCachedChunk(jobState, g.WindowStart, g.WindowEnd))
                    .Distinct()
                    .ToList();
                if (savedWindows.Count > 0)
                {
                    foreach (var saved in savedWindows)
                    {
                        usedChunks.Add(saved);
                        readings.Add(new Reading(saved, ReadingKind.Gap));
                        var savedGap = jobState.GapRetries.First(g => FindCachedChunk(jobState, g.WindowStart, g.WindowEnd) == saved);
                        report.Requests.Add(new RequestToDo(new Timing(saved.StartTime, saved.EndTime), "gap retry of a previous run", ReadingKind.Gap)
                        {
                            Result = saved,
                            FromCache = true,
                            HoleStart = savedGap.HoleStart,
                            HoleEnd = savedGap.HoleEnd,
                            Note = "saved in GapRetries by a previous run"
                        });
                    }
                    currentItems = RebuildItems(context, transcription, readings, previousTypeItems, searchStart, audioEnd, report, isFinal:false);
                    context.WriteInfo($"Gap retries of the previous runs: {savedWindows.Count} request(s), from the job state.");
                }

                for (var round = 1; round <= MaxGapRetryRounds; round++)
                {
                    var tried = jobState.GapRetries.Select(g => (g.HoleStart, g.HoleEnd)).ToList();
                    var holes = FindGaps(currentItems, searchStart, audioEnd, retryGapMinDuration)
                        .SelectMany(h => Subtract(h, tried))
                        .Where(h => h.End - h.Start >= retryGapMinDuration)
                        .ToList();
                    if (holes.Count == 0)
                        break;

                    var windows = BuildRetryWindows(holes, searchStart, audioEnd);
                    var todos = windows
                        .Select(w => new RequestToDo(
                            new Timing(w.WindowStart, w.WindowEnd),
                            $"gap {w.HoleStart:hh\\:mm\\:ss\\.f} to {w.HoleEnd:hh\\:mm\\:ss\\.f} (audio {w.WindowStart:hh\\:mm\\:ss\\.f} to {w.WindowEnd:hh\\:mm\\:ss\\.f})",
                            ReadingKind.Gap)
                        { Round = round, HoleStart = w.HoleStart, HoleEnd = w.HoleEnd })
                        .ToList();
                    var budgetReached = false;
                    var items = RunRetries(
                        todos,
                        $"Gap retries, round {round} ({holes.Count} gap(s) of at least {retryGapMinDuration.TotalSeconds:0.#} s)",
                        onStored: () =>
                        {
                            // (also an answer found in the job state, ex. of a run of an older version: the gap is asked)
                            for (var index = 0; index < todos.Count; index++)
                            {
                                var window = windows[index];
                                if (todos[index].Result != null
                                    && !jobState.GapRetries.Any(g => g.HoleStart == window.HoleStart && g.HoleEnd == window.HoleEnd && g.WindowStart == window.WindowStart && g.WindowEnd == window.WindowEnd))
                                {
                                    jobState.GapRetries.Add(new GapRetry
                                    {
                                        HoleStart = windows[index].HoleStart,
                                        HoleEnd = windows[index].HoleEnd,
                                        WindowStart = windows[index].WindowStart,
                                        WindowEnd = windows[index].WindowEnd
                                    });
                                }
                            }
                        },
                        onBudget: () => budgetReached = true);
                    currentItems = items ?? currentItems;
                    if (budgetReached)
                        break;
                }
            }

            context.RunStep("Build the subtitles from the words", _ => RebuildItems(context, transcription, readings, previousTypeItems, searchStart, audioEnd, report, isFinal:true));
            transcription.MarkAsFinished();
            context.WIP.Save();

            // (safe point: the previous answers are saved)
            void StopOrPauseIfAsked()
            {
                if (isStopAfterRequested)
                {
                    throw new UserStoppedWorkerException("Stopped after the request, as asked by the user.");
                }
                context.PauseIfClosing();
            }

            // Retries (boundaries, gaps): sent in parallel, then the subtitles built again (null: nothing to do)
            List<TimedItemWithMetadata> RunRetries(List<RequestToDo> todos, string title, Action onStored = null, Action onBudget = null)
            {
                if (todos.Count == 0)
                    return null;
                var nbNew = todos.Count(t => FindCachedChunk(jobState, t.Timing.StartTime, t.Timing.EndTime) == null);
                if (nbNew > 0)
                {
                    context.WriteInfo($"{title}: {todos.Count} request(s), {nbNew} to send ({Math.Min(nbNew, this.MaxParallelRequests)} at a time).");
                }
                bool withinBudget;
                string budgetMessage;
                try
                {
                    withinBudget = RunRequests(todos, out budgetMessage);
                }
                finally
                {
                    // (what was received is kept, even if a request failed: the gaps asked are saved with their answer)
                    foreach (var todo in todos.Where(t => t.Result != null))
                    {
                        if (!readings.Any(r => r.Chunk == todo.Result))
                            readings.Add(new Reading(todo.Result, todo.Kind));
                    }
                    onStored?.Invoke();
                    if (nbNew > 0)
                        context.WIP.Save();
                }
                if (!withinBudget)
                {
                    context.WriteInfo($"{title}: stopped, {budgetMessage}.", ConsoleColor.Yellow);
                    onBudget?.Invoke();
                }

                var beforeCount = transcription.Items.Count;
                var items = context.RunStep("Build the subtitles from the words", _ => RebuildItems(context, transcription, readings, previousTypeItems, searchStart, audioEnd, report, isFinal:false));
                if (nbNew > 0)
                {
                    context.WriteInfo($"{title}: {transcription.Items.Count - beforeCount:+#;-#;0} subtitle(s) (total: {transcription.Items.Count}).");
                    context.WIP.Save();
                }
                StopOrPauseIfAsked();
                return items;
            }

            // The answers of these requests: from the job state, or sent (at most MaxParallelRequests at a time; the answers are stored here, on
            // this thread). False: some were not sent, they would exceed MaxTotalAudioFactor.
            bool RunRequests(IList<RequestToDo> todos, out string budgetMessage)
            {
                budgetMessage = null;
                var toSend = new List<RequestToDo>();
                var pendingAudio = TimeSpan.Zero;
                foreach (var todo in todos)
                {
                    if (!report.Requests.Contains(todo))
                        report.Requests.Add(todo);
                    var cached = FindCachedChunk(jobState, todo.Timing.StartTime, todo.Timing.EndTime);
                    if (cached != null)
                    {
                        todo.Result = cached;
                        todo.FromCache = true;
                        usedChunks.Add(cached);
                        continue;
                    }
                    var usedAudio = TimeSpan.FromTicks(usedChunks.Sum(c => Math.Max(0, (c.EndTime - c.StartTime).Ticks)));
                    if (usedAudio + pendingAudio + todo.Timing.Duration > maxAudioBudget)
                    {
                        budgetMessage ??= $"the transcribed audio ({usedAudio + pendingAudio + todo.Timing.Duration:hh\\:mm\\:ss}) would exceed 'MaxTotalAudioFactor' ({this.MaxTotalAudioFactor:0.##}x = {maxAudioBudget:hh\\:mm\\:ss})";
                        todo.NotSent = "budget (MaxTotalAudioFactor)";
                        continue;
                    }
                    pendingAudio += todo.Timing.Duration;
                    toSend.Add(todo);
                }

                var running = new List<(RequestToDo Todo, Task<AIEngineAPIGoogle.TranscribeResult> Task)>();
                Exception error = null;
                var nextIndex = 0;
                while (true)
                {
                    // (not after an error or a 'Stop after this request': the ones running end, the others are not sent)
                    while (error == null && !isStopAfterRequested && running.Count < this.MaxParallelRequests && nextIndex < toSend.Count)
                    {
                        if (running.Count == 0)
                            context.PauseIfClosing();
                        // (the audio of the request: made when it's sent, not all of them in advance)
                        var todo = toSend[nextIndex++];
                        var request = PrepareRequest(todo);
                        running.Add((todo, Task.Run(() => SendRequest(todo, request))));
                    }
                    if (running.Count == 0)
                        break;
                    var index = Task.WaitAny(running.Select(r => (Task)r.Task).ToArray());
                    var (doneTodo, doneTask) = running[index];
                    running.RemoveAt(index);
                    if (doneTask.IsFaulted)
                    {
                        error ??= doneTask.Exception.InnerException;
                        doneTodo.NotSent = $"failed: {doneTask.Exception.InnerException?.Message}";
                    }
                    else
                        StoreResult(doneTodo, doneTask.Result);
                }
                for (var index = nextIndex; index < toSend.Count; index++)
                {
                    toSend[index].NotSent = error != null ? "not sent: another request failed" : "not sent: 'stop after the request'";
                }
                if (error != null)
                {
                    ExceptionDispatchInfo.Capture(error).Throw();
                }
                return budgetMessage == null;
            }

            AIRequest PrepareRequest(RequestToDo todo)
            {
                var prefix = todo.Kind == ReadingKind.Chunk ? string.Empty : todo.Kind == ReadingKind.Boundary ? "boundary_" : "gap_";
                var fileName = $"{prefix}{todo.Timing.StartTime:hh\\-mm\\-ss\\-fff}.wav";
                var requestDescription = todo.Kind == ReadingKind.Chunk
                    ? $"{todo.Timing.StartTime:hh\\:mm\\:ss\\.fff} to {todo.Timing.EndTime:hh\\:mm\\:ss\\.fff} out of {context.WIP.TimelineMap.Duration:hh\\:mm\\:ss\\.fff}"
                    : todo.Name;
                var number = requestNumber++;
                return context.RunStep($"Prepare {todo.Name}", _ =>
                {
                    var snippet = fullPcmAudio.ExtractSnippet(todo.Timing.StartTime, todo.Timing.EndTime);
                    var audioBytes = snippet.ToWavBytes();
                    if (this.KeepTemporaryFiles)
                        context.CreateVerboseBinaryFile($"{transcription.Id}_{fileName}", audioBytes, processStartTime);
                    var userParts = new AIRequestPartCollection();
                    userParts.Add(new AIRequestPartAudio(AIRequestSection.PrimaryNodes, fileName, audioBytes, snippet.Duration, todo.Timing.StartTime));
                    return new AIRequest(
                        processStartTime,
                        number,
                        this.Id,
                        null,
                        new AIRequestPartCollection(),
                        userParts,
                        this.MetadataProduced,
                        requestDescription,
                        todo.Timing.StartTime);
                });
            }

            // (on a thread of its own: only the engine and the task of the request, nothing of the job state)
            AIEngineAPIGoogle.TranscribeResult SendRequest(RequestToDo todo, AIRequest request)
            {
                using var requestNode = context.BeginTask(todo.Kind == ReadingKind.Chunk ? "AI request" : "AI request (retry)", todo.Name);
                requestNode.SetCancellable(AIEngineAPI.StopNowHint);
                requestNode.SetPauseAfterSupported();
                requestNode.SetStopNowWarning(AIEngineAPI.StopNowCostWarning);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var result = this.Engine.Transcribe(context, request, options);
                    todo.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 1);
                    if (requestNode.IsStopAfterRequested)
                        isStopAfterRequested = true;
                    return result;
                }
                catch (UserStoppedWorkerException)
                {
                    requestNode.SetState(TaskState.Cancelled, "stopped by the user");
                    throw;
                }
                catch (NeedsUserException ex)
                {
                    // (not sent: the step waits for the user, ex. an API key)
                    requestNode.SetState(TaskState.Skipped, ex.Message);
                    throw;
                }
                catch (Exception ex)
                {
                    requestNode.Fail(ex.Message);
                    throw;
                }
            }

            void StoreResult(RequestToDo todo, AIEngineAPIGoogle.TranscribeResult result)
            {
                var chunk = new CachedChunk
                {
                    StartTime = todo.Timing.StartTime,
                    EndTime = todo.Timing.EndTime,
                    Text = result.Text,
                    Words = result.Words,
                    IncompleteReason = result.IncompleteReason
                };
                jobState.Chunks.Add(chunk);
                usedChunks.Add(chunk);
                todo.Result = chunk;
                todo.Cost = result.Cost;
                if (IsLooping(chunk))
                {
                    context.WriteInfo($"{todo.Name}: the answer is ignored, {chunk.Words.Length} words for {todo.Timing.Duration:hh\\:mm\\:ss} of audio (the model repeated itself).", ConsoleColor.Yellow);
                }

                if (result.Cost != null)
                {
                    dynamic customInfos = result.Cost.CustomInfos ?? new ExpandoObject();
                    customInfos.ChunkStartTime = $"{todo.Timing.StartTime:hh\\:mm\\:ss\\.fff}";
                    customInfos.ChunkEndTime = $"{todo.Timing.EndTime:hh\\:mm\\:ss\\.fff}";
                    customInfos.Duration = $"{todo.Timing.Duration:hh\\:mm\\:ss\\.fff}";
                    if (todo.Kind != ReadingKind.Chunk)
                        customInfos.Retry = todo.Kind.ToString();
                    result.Cost.CustomInfos = customInfos;
                    if (todo.Kind != ReadingKind.Chunk)
                        result.Cost.NbItemsInResponse = BuildPhrases(context, chunk, logIgnored: false).Count;
                    transcription.Costs.Add(result.Cost);
                }
            }
        }

        private static CachedChunk FindCachedChunk(JobState jobState, TimeSpan startTime, TimeSpan endTime)
        {
            return jobState.Chunks.FirstOrDefault(c =>
                Math.Abs((c.StartTime - startTime).TotalMilliseconds) < 1.0 &&
                Math.Abs((c.EndTime - endTime).TotalMilliseconds) < 1.0);
        }

        /// <summary>The model repeated itself in this answer (far too many words for its audio).</summary>
        private static bool IsLooping(CachedChunk chunk)
        {
            var seconds = (chunk.EndTime - chunk.StartTime).TotalSeconds;
            return seconds > 0 && (chunk.Words?.Length ?? 0) / seconds > LoopingMinWordsPerSecond;
        }

        /// <summary>
        /// The words of the answer that can be used: none when the model repeated itself (far too many words), without the words with impossible
        /// times, without the repetition at the end of the answer (see MinRepetitionsToCut).
        /// </summary>
        private static List<AIEngineAPIGoogle.TranscribedWordInfo> GetUsableWords(CachedChunk chunk, out int nbBroken, out int nbRepeated)
        {
            nbRepeated = 0;
            if (IsLooping(chunk))
            {
                nbBroken = 0;
                return new List<AIEngineAPIGoogle.TranscribedWordInfo>();
            }
            var words = RemoveBrokenWords(chunk.Words ?? Array.Empty<AIEngineAPIGoogle.TranscribedWordInfo>(), out nbBroken);
            var cutAt = GetRepeatingTailStart(words);
            nbRepeated = words.Count - cutAt;
            if (nbRepeated > 0)
                words.RemoveRange(cutAt, nbRepeated);
            return words;
        }

        /// <summary>Where the repetition at the end of the answer starts (the number of words: none).</summary>
        private static int GetRepeatingTailStart(List<AIEngineAPIGoogle.TranscribedWordInfo> words)
        {
            static string Normalize(string text) => new string((text ?? "").Where(char.IsLetterOrDigit).ToArray());
            static bool IsSameCharacterRepeated(string text)
            {
                for (int i = 0, run = 1; i + 1 < text.Length; i++)
                {
                    run = text[i + 1] == text[i] ? run + 1 : 1;
                    if (run >= MinRepetitionsToCut)
                        return true;
                }
                return false;
            }
            if (words.Count == 0)
                return 0;
            var cutAt = words.Count;
            // (the same word again and again, until the end)
            var last = Normalize(words[^1].Text);
            if (last.Length > 0)
            {
                var start = words.Count - 1;
                while (start > 0 && Normalize(words[start - 1].Text) == last)
                    start--;
                if (words.Count - start >= MinRepetitionsToCut)
                    cutAt = start;
            }
            // (one of the last words made of the same character again and again)
            for (var index = words.Count - 1; index >= Math.Max(0, words.Count - 3); index--)
            {
                if (IsSameCharacterRepeated(Normalize(words[index].Text)))
                    cutAt = Math.Min(cutAt, index);
            }
            return cutAt;
        }

        /// <summary>A pause longer than this between two words ends a series of words (where the next chunk starts: a fixed rule, not PhraseMaxGap).</summary>
        private static readonly TimeSpan WordRunMaxGap = TimeSpan.FromMilliseconds(300);

        /// <summary>
        /// The series of words of the answer (in the scene, in the order of the time): the words without a pause of more than WordRunMaxGap between
        /// them, all the words included (also the ones of TranscriptionToIgnorePatterns). Only the guards of the words (GetUsableWords) change them.
        /// </summary>
        private static List<(TimeSpan Start, TimeSpan End)> GetWordRuns(CachedChunk chunk)
        {
            var runs = new List<(TimeSpan Start, TimeSpan End)>();
            foreach (var word in GetUsableWords(chunk, out _, out _).OrderBy(w => w.StartTime))
            {
                var start = chunk.StartTime + word.StartTime;
                var end = chunk.StartTime + word.EndTime;
                if (runs.Count > 0 && start - runs[^1].End <= WordRunMaxGap)
                    runs[^1] = (runs[^1].Start, TimeSpanExtensions.Max(runs[^1].End, end));
                else
                    runs.Add((start, end));
            }
            return runs;
        }

        /// <summary>The end of the last word of the answer, all its words included (also the ones of TranscriptionToIgnorePatterns: the model was still writing), in the scene (null: no word).</summary>
        private static TimeSpan? GetLastWordEnd(CachedChunk chunk)
        {
            var words = GetUsableWords(chunk, out _, out _);
            return words.Count == 0 ? (TimeSpan?)null : chunk.StartTime + words.Max(w => w.EndTime);
        }

        /// <summary>The gaps of at least minGap between the subtitles (sorted by their start), from 'from' to 'to'.</summary>
        private static List<(TimeSpan Start, TimeSpan End)> FindGaps(List<TimedItemWithMetadata> items, TimeSpan from, TimeSpan to, TimeSpan minGap)
        {
            var gaps = new List<(TimeSpan Start, TimeSpan End)>();
            var previousEnd = from;
            foreach (var item in items.OrderBy(i => i.StartTime))
            {
                if (item.StartTime - previousEnd >= minGap)
                    gaps.Add((previousEnd, item.StartTime));
                if (item.EndTime > previousEnd)
                    previousEnd = item.EndTime;
            }
            if (to - previousEnd >= minGap)
                gaps.Add((previousEnd, to));
            return gaps;
        }

        /// <summary>The parts of the gap that are not in one of these intervals.</summary>
        private static IEnumerable<(TimeSpan Start, TimeSpan End)> Subtract((TimeSpan Start, TimeSpan End) gap, List<(TimeSpan Start, TimeSpan End)> intervals)
        {
            var parts = new List<(TimeSpan Start, TimeSpan End)> { gap };
            foreach (var (start, end) in intervals)
            {
                parts = parts.SelectMany(p =>
                {
                    if (end <= p.Start || start >= p.End)
                        return new[] { p };
                    var left = start > p.Start ? new[] { (p.Start, start) } : Array.Empty<(TimeSpan, TimeSpan)>();
                    var right = end < p.End ? new[] { (end, p.End) } : Array.Empty<(TimeSpan, TimeSpan)>();
                    return left.Concat(right).ToArray();
                }).ToList();
            }
            return parts;
        }

        /// <summary>
        /// The requests for these gaps: RetryPreRoll before, RetryPostRoll after, at most RetryWindowMaxDuration each (a longer gap: cut in
        /// equal parts, one request each; close gaps: one request together).
        /// </summary>
        private List<(TimeSpan WindowStart, TimeSpan WindowEnd, TimeSpan HoleStart, TimeSpan HoleEnd)> BuildRetryWindows(
            List<(TimeSpan Start, TimeSpan End)> holes,
            TimeSpan from,
            TimeSpan audioEnd)
        {
            var room = this.RetryWindowMaxDuration - this.RetryPreRoll - this.RetryPostRoll;
            var windows = new List<(TimeSpan WindowStart, TimeSpan WindowEnd, TimeSpan HoleStart, TimeSpan HoleEnd)>();
            foreach (var (holeStart, holeEnd) in holes.OrderBy(h => h.Start))
            {
                var nbParts = (int)Math.Ceiling((holeEnd - holeStart).Ticks / (double)room.Ticks);
                var partDuration = TimeSpan.FromTicks((holeEnd - holeStart).Ticks / Math.Max(1, nbParts));
                for (var part = 0; part < nbParts; part++)
                {
                    var partStart = holeStart + TimeSpan.FromTicks(partDuration.Ticks * part);
                    var partEnd = part == nbParts - 1 ? holeEnd : partStart + partDuration;
                    // (on another grid of audio tokens than the main passes)
                    var windowStart = TimeSpanExtensions.Max(from, partStart - this.RetryPreRoll) + this.RetryGridShift;
                    var windowEnd = TimeSpanExtensions.Min(audioEnd, partEnd + this.RetryPostRoll + this.RetryGridShift);
                    if (windows.Count > 0)
                    {
                        var previous = windows[^1];
                        if (windowStart <= previous.WindowEnd && TimeSpanExtensions.Max(windowEnd, previous.WindowEnd) - previous.WindowStart <= this.RetryWindowMaxDuration)
                        {
                            windows[^1] = (previous.WindowStart, TimeSpanExtensions.Max(windowEnd, previous.WindowEnd), previous.HoleStart, TimeSpanExtensions.Max(partEnd, previous.HoleEnd));
                            continue;
                        }
                    }
                    windows.Add((windowStart, windowEnd, partStart, partEnd));
                }
            }
            return windows;
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
        /// A phrase of another answer with the same text (or one containing the other) that starts at most this far: the same phrase, placed a bit
        /// elsewhere (seen: ~2 s apart between two requests), a duplicate.
        /// </summary>
        private static readonly TimeSpan SameTextMaxDistance = TimeSpan.FromSeconds(3);

        /// <summary>The same text (letters / digits only), or one contains the other (at least 2 characters).</summary>
        private static bool IsSameText(string a, string b)
        {
            static string Normalize(string text) => new string((text ?? "").Where(char.IsLetterOrDigit).ToArray());
            var na = Normalize(a);
            var nb = Normalize(b);
            if (na.Length == 0 || nb.Length == 0)
                return false;
            if (na == nb)
                return true;
            var (shorter, longer) = na.Length <= nb.Length ? (na, nb) : (nb, na);
            return shorter.Length >= 2 && longer.Contains(shorter);
        }

        /// <summary>The two subtitles overlap (with OverlapRecoveryMargin).</summary>
        private static bool Overlaps(TimedItemWithMetadata a, TimedItemWithMetadata b)
        {
            return a.EndTime + OverlapRecoveryMargin > b.StartTime && a.StartTime - OverlapRecoveryMargin < b.EndTime;
        }

        /// <summary>How far (in seconds) the line is from the start / end of the audio sent, the closest of the two (the start / end of the whole audio: not a limit).</summary>
        private static double GetScore(TimedItemWithMetadata item, CachedChunk chunk, TimeSpan audioEnd)
        {
            var fromStart = chunk.StartTime <= TimeSpan.Zero ? double.MaxValue : (item.StartTime - chunk.StartTime).TotalSeconds;
            var toEnd = chunk.EndTime >= audioEnd - TimeSpan.FromMilliseconds(1) ? double.MaxValue : (chunk.EndTime - item.EndTime).TotalSeconds;
            return Math.Min(fromStart, toEnd);
        }

        /// <summary>
        /// The line (of a request less reliable there) should replace these lines of other requests: they are all inside it, and they leave
        /// a good part of its speech (the times of its words, not its pauses) without a line (the other requests cut that speech in shorter
        /// lines, and missed some of them).
        /// </summary>
        private static bool IsLongerLineToKeep(Candidate line, List<TimedItemWithMetadata> others)
        {
            if (!others.All(o => o.StartTime >= line.Item.StartTime - OverlapRecoveryMargin && o.EndTime <= line.Item.EndTime + OverlapRecoveryMargin))
                return false;
            var speech = TimeSpan.Zero;
            var uncovered = TimeSpan.Zero;
            foreach (var (wordStart, wordEnd) in line.WordSpans)
            {
                speech += wordEnd - wordStart;
                // (the parts of the word outside of the other lines)
                foreach (var (start, end) in Subtract((wordStart, wordEnd), others.Select(o => (o.StartTime, o.EndTime)).ToList()))
                    uncovered += end - start;
            }
            return uncovered >= MinUncoveredForLongerLine && uncovered.Ticks * 2 >= speech.Ticks;
        }

        /// <summary>
        /// The subtitles of the transcription, built again from all the answers (with the rules of now), after the ones of the previous type of
        /// the step, if any. The answers are cut in phrases (see PhraseMaxGap); each phrase comes from the answer where it's farthest from the
        /// start / end of the audio sent: the phrases are taken in that order, a phrase that overlaps a phrase already taken from another answer
        /// is left out (except a longer one, see IsLongerLineToKeep). The phrases of the same answer don't exclude each other (ex. the same line
        /// by 2 speakers). Then the subtitles are built from the words of the phrases kept (MaxGap, EndOfSentenceMinGap, MaxDuration, the speakers).
        /// Returns the subtitles without the end padding (to find the gaps).
        /// </summary>
        private List<TimedItemWithMetadata> RebuildItems(
            SubtitleGeneratorContext context,
            Track transcription,
            List<Reading> readings,
            List<TimedItemWithMetadata> previousTypeItems,
            TimeSpan searchStart,
            TimeSpan audioEnd,
            RunReport report,
            bool isFinal)
        {
            var candidates = new List<Candidate>();
            for (var index = 0; index < readings.Count; index++)
            {
                var reading = readings[index];
                foreach (var phrase in BuildPhrases(context, reading.Chunk, logIgnored: isFinal && reading.Kind == ReadingKind.Chunk))
                {
                    // (not in the part of the subtitles of the previous type)
                    if (phrase.Item.StartTime < searchStart)
                        continue;
                    candidates.Add(new Candidate(phrase, index, reading.Kind, GetScore(phrase.Item, reading.Chunk, audioEnd)));
                }
            }

            var accepted = new List<Candidate>();
            var nbLongerLines = 0;
            var nbDuplicates = 0;
            var duplicates = new List<(Candidate Dropped, CachedChunk DroppedChunk, Candidate Kept, CachedChunk KeptChunk)>();
            foreach (var candidate in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.ReadingIndex).ThenBy(c => c.Item.StartTime))
            {
                var conflicts = accepted.Where(a => a.ReadingIndex != candidate.ReadingIndex && Overlaps(a.Item, candidate.Item)).ToList();
                // (the same phrase, placed a bit elsewhere by another answer: a duplicate, left out)
                var sameText = conflicts.Count == 0 ? accepted.FirstOrDefault(a => a.ReadingIndex != candidate.ReadingIndex
                    && (a.Item.StartTime - candidate.Item.StartTime).Duration() <= SameTextMaxDistance
                    && IsSameText(a.Item.Metadata.Get(this.MetadataProduced), candidate.Item.Metadata.Get(this.MetadataProduced))) : null;
                if (sameText != null)
                {
                    nbDuplicates++;
                    duplicates.Add((candidate, readings[candidate.ReadingIndex].Chunk, sameText, readings[sameText.ReadingIndex].Chunk));
                }
                else if (conflicts.Count == 0)
                {
                    accepted.Add(candidate);
                }
                else if (IsLongerLineToKeep(candidate, conflicts.Select(c => c.Item).ToList()))
                {
                    accepted.RemoveAll(conflicts.Contains);
                    accepted.Add(candidate);
                    nbLongerLines++;
                }
            }
            report.LastAccepted = accepted.Select(a => (a, readings[a.ReadingIndex].Chunk)).ToList();
            report.NbLongerLines = nbLongerLines;
            report.NbDuplicates = nbDuplicates;
            report.Duplicates = duplicates;
            if (isFinal && readings.Any(r => r.Kind != ReadingKind.Chunk))
            {
                context.WriteInfo($"Phrases from: the chunks {accepted.Count(a => a.Kind == ReadingKind.Chunk)}, the boundary retries {accepted.Count(a => a.Kind == ReadingKind.Boundary)}, " +
                    $"the gap retries {accepted.Count(a => a.Kind == ReadingKind.Gap)}" +
                    (nbLongerLines > 0 ? $"; {nbLongerLines} longer phrase(s) kept instead of shorter ones of another request" : "") +
                    (nbDuplicates > 0 ? $"; {nbDuplicates} phrase(s) left out, the same text near the same place in another request" : "") + ".");
            }

            // The subtitles, from the words of the phrases kept (in the order of the time of the phrases; the words of a phrase stay together: with
            // several speakers, the model gives the words speaker by speaker, not in the order of the time)
            var built = BuildLines(context, accepted.OrderBy(a => a.Item.StartTime).ThenBy(a => a.ReadingIndex).Select(a => a.Phrase).ToList(), logIgnored: isFinal);

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
            report.LastItems = merged;
            return merged;
        }

        /// <summary>Debug, for the tests of the retrier: what a run did (see WriteRunReport).</summary>
        private sealed class RunReport
        {
            public DateTime StartedAt { get; } = DateTime.Now;
            public TimeSpan AudioEnd { get; set; }
            public TimeSpan SearchStart { get; set; }
            public JobState JobState { get; set; }
            /// <summary>All the requests of the run, in the order they were asked (from the job state or sent).</summary>
            public List<RequestToDo> Requests { get; } = new List<RequestToDo>();
            /// <summary>The lines of the last build of the subtitles, with the answer each one comes from.</summary>
            public List<(Candidate Candidate, CachedChunk Chunk)> LastAccepted { get; set; } = new List<(Candidate, CachedChunk)>();
            public int NbLongerLines { get; set; }
            public int NbDuplicates { get; set; }
            public List<(Candidate Dropped, CachedChunk DroppedChunk, Candidate Kept, CachedChunk KeptChunk)> Duplicates { get; set; } = new List<(Candidate, CachedChunk, Candidate, CachedChunk)>();
            public List<TimedItemWithMetadata> LastItems { get; set; } = new List<TimedItemWithMetadata>();
        }

        /// <summary>
        /// Debug, for the tests of the retrier: '<scene>.Worker.<id>.run.json' (with ExportMetadataSrt), at the end of a run (also when stopped /
        /// failed): the settings, every request (from the job state or sent, its words, its lines kept...), the final lines with the request they
        /// come from, the gaps left.
        /// </summary>
        private void WriteRunReport(SubtitleGeneratorContext context, Track transcription, RunReport report, string status)
        {
            if (!this.ExportMetadataSrt || report.JobState == null)
                return;
            try
            {
                static string T(TimeSpan t) => t.ToString(@"hh\:mm\:ss\.fff");
                static double Cost(Cost cost) => cost == null ? 0 : cost.Input.EstimatedCost + cost.Output.EstimatedCost;
                var keptByChunk = report.LastAccepted.GroupBy(a => a.Chunk).ToDictionary(g => g.Key, g => g.Count());
                var indexByChunk = new Dictionary<CachedChunk, int>();
                var requests = new List<object>();
                for (var index = 0; index < report.Requests.Count; index++)
                {
                    var r = report.Requests[index];
                    var chunk = r.Result;
                    if (chunk != null && !indexByChunk.ContainsKey(chunk))
                        indexByChunk[chunk] = index;
                    var nbRepeated = 0;
                    var words = chunk == null ? null : GetUsableWords(chunk, out _, out nbRepeated);
                    requests.Add(new
                    {
                        Index = index,
                        Kind = r.Kind.ToString(),
                        r.Pass,
                        r.Round,
                        Start = T(r.Timing.StartTime),
                        End = T(r.Timing.EndTime),
                        Duration = T(r.Timing.Duration),
                        Hole = r.HoleStart is TimeSpan hs && r.HoleEnd is TimeSpan he ? $"{T(hs)} - {T(he)}" : null,
                        Source = chunk == null ? "not sent" : r.FromCache ? "cache" : "sent",
                        r.NotSent,
                        r.Seconds,
                        Status = chunk == null ? null : IsLooping(chunk) ? "looping (ignored)" : chunk.IncompleteReason != null ? $"incomplete: {chunk.IncompleteReason}" : "completed",
                        Words = chunk?.Words?.Length,
                        FirstWord = words != null && words.Count > 0 ? T(chunk.StartTime + words.Min(w => w.StartTime)) : null,
                        LastWord = words != null && words.Count > 0 ? T(chunk.StartTime + words.Max(w => w.EndTime)) : null,
                        WordsCutAsRepetition = nbRepeated > 0 ? nbRepeated : (int?)null,
                        Phrases = chunk == null ? (int?)null : BuildPhrases(context, chunk, logIgnored: false).Count,
                        PhrasesKept = chunk != null && keptByChunk.TryGetValue(chunk, out var kept) ? kept : 0,
                        r.Note,
                        Cost = r.FromCache || r.Cost == null ? (double?)null : Math.Round(Cost(r.Cost), 5)
                    });
                }

                var tried = report.JobState.GapRetries.Select(g => (g.HoleStart, g.HoleEnd)).ToList();
                var gapsLeft = this.RetryGapMinDuration is TimeSpan minGap
                    ? FindGaps(report.LastItems, report.SearchStart, report.AudioEnd, minGap)
                        .Select(g => new
                        {
                            Start = T(g.Start),
                            End = T(g.End),
                            Duration = T(g.End - g.Start),
                            AlreadyAsked = Subtract(g, tried).Sum(p => (p.End - p.Start).Ticks) < (g.End - g.Start).Ticks / 2
                        })
                        .ToList<object>()
                    : null;

                var sent = report.Requests.Where(r => r.Result != null && !r.FromCache).ToList();
                var json = new
                {
                    Step = this.Id,
                    Status = status,
                    StartedAt = report.StartedAt,
                    EndedAt = DateTime.Now,
                    Settings = new
                    {
                        MaxChunkDuration = T(this.MaxChunkDuration),
                        this.MainPassCount,
                        BufferZone = T(this.BufferZone),
                        EarlyStopMinDuration = this.EarlyStopMinDuration is TimeSpan e ? T(e) : null,
                        BoundaryRetryMargin = this.BoundaryRetryMargin is TimeSpan b ? T(b) : null,
                        RetryGapMinDuration = this.RetryGapMinDuration is TimeSpan g ? T(g) : null,
                        RetryPreRoll = T(this.RetryPreRoll),
                        RetryPostRoll = T(this.RetryPostRoll),
                        RetryWindowMaxDuration = T(this.RetryWindowMaxDuration),
                        RetryGridShiftMs = this.RetryGridShift.TotalMilliseconds,
                        this.MaxParallelRequests,
                        this.MaxTotalAudioFactor,
                        this.MaxGap,
                        this.EndOfSentenceMinGap,
                        this.MaxDuration,
                        NbIgnorePatterns = this.TranscriptionToIgnorePatterns?.Length ?? 0
                    },
                    Audio = new { Duration = T(report.AudioEnd), SearchStart = T(report.SearchStart) },
                    Totals = new
                    {
                        Requests = report.Requests.Count,
                        ByKind = report.Requests.GroupBy(r => r.Kind.ToString()).ToDictionary(k => k.Key, k => new
                        {
                            Total = k.Count(),
                            Sent = k.Count(r => r.Result != null && !r.FromCache),
                            Cache = k.Count(r => r.Result != null && r.FromCache),
                            NotSent = k.Count(r => r.Result == null),
                            PhrasesKept = k.Where(r => r.Result != null).Select(r => r.Result).Distinct().Sum(c => keptByChunk.TryGetValue(c, out var n) ? n : 0)
                        }),
                        AudioSent = T(TimeSpan.FromTicks(sent.Sum(r => r.Timing.Duration.Ticks))),
                        EstimatedCost = Math.Round(sent.Sum(r => Cost(r.Cost)), 4),
                        PhrasesKept = report.LastAccepted.Count,
                        LongerPhrasesKept = report.NbLongerLines,
                        DuplicatePhrasesLeftOut = report.NbDuplicates,
                        Subtitles = report.LastItems.Count,
                        JobStateChunks = report.JobState.Chunks.Count,
                        JobStateGapRetries = report.JobState.GapRetries.Count
                    },
                    Requests = requests,
                    GapsLeft = gapsLeft,
                    // (the subtitles built from the phrases kept, before EndPadding)
                    Subtitles = report.LastItems.Select(s => new { Start = T(s.StartTime), End = T(s.EndTime), Text = s.Metadata.Get(this.MetadataProduced) }).ToList(),
                    // (the phrases kept, with the request they come from)
                    PhrasesKept = report.LastAccepted
                        .OrderBy(a => a.Candidate.Item.StartTime)
                        .Select(a => new
                        {
                            Start = T(a.Candidate.Item.StartTime),
                            End = T(a.Candidate.Item.EndTime),
                            Text = a.Candidate.Item.Metadata.Get(this.MetadataProduced),
                            Kind = a.Candidate.Kind.ToString(),
                            Request = indexByChunk.TryGetValue(a.Chunk, out var i) ? i : (int?)null,
                            Score = a.Candidate.Score == double.MaxValue ? (double?)null : Math.Round(a.Candidate.Score, 1)
                        })
                        .ToList()
                };
                var path = context.WIP.BaseFilePath + $".Worker.{transcription.Id}.run.json";
                System.IO.File.WriteAllText(path, JsonConvert.SerializeObject(json, Formatting.Indented), new UTF8Encoding(false));

                if (!string.IsNullOrWhiteSpace(this.GroundTruthExtension))
                    WriteGroundTruthReport(context, transcription, report, status);
            }
            catch (Exception ex)
            {
                context.WriteLog($"The run report can't be written: {ex}");
            }
        }

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

        /// <summary>
        /// The phrases of an answer (see PhraseMaxGap): its words, in the order of the answer, cut where the pause between two words is longer
        /// than PhraseMaxGap (or the speaker changes, with Diarization). The phrases that match TranscriptionToIgnorePatterns are left out.
        /// </summary>
        private List<Phrase> BuildPhrases(SubtitleGeneratorContext context, CachedChunk chunk, bool logIgnored)
        {
            var words = GetUsableWords(chunk, out var nbBroken, out var nbRepeated);
            if (nbBroken > 0 && logIgnored)
                context.WriteLog($"Chunk {chunk.StartTime:hh\\:mm\\:ss}: {nbBroken} word(s) with impossible times ignored.");
            if (nbRepeated > 0 && logIgnored)
                context.WriteLog($"Chunk {chunk.StartTime:hh\\:mm\\:ss}: the last {nbRepeated} word(s) ignored, the model repeated itself.");

            var groups = new List<List<AIEngineAPIGoogle.TranscribedWordInfo>>();
            List<AIEngineAPIGoogle.TranscribedWordInfo> current = null;
            foreach (var word in words)
            {
                if (current != null
                    && (word.StartTime - current[^1].EndTime > this.PhraseMaxGap || (this.Diarization && word.Speaker != current[^1].Speaker)))
                {
                    current = null;
                }
                if (current == null)
                {
                    current = new List<AIEngineAPIGoogle.TranscribedWordInfo>();
                    groups.Add(current);
                }
                current.Add(word);
            }

            var phrases = new List<Phrase>();
            foreach (var group in groups)
            {
                var text = ExtractText(group.Select(w => (chunk, w)).ToList());
                if (text.Length == 0)
                    continue;
                var startTime = chunk.StartTime + group.Min(w => w.StartTime);
                var pattern = this.TranscriptionToIgnorePatterns?.FirstOrDefault(p => p.Regex.IsMatch(text));
                if (pattern != null)
                {
                    if (logIgnored)
                        context.WriteLog($"Ignoring '{pattern.Name}' phrase at [{context.WIP.TimelineMap.ConvertToPartSpecificFileIndexAndTime(startTime)}]: {text}");
                    continue;
                }
                var metadata = new MetadataCollection();
                metadata[this.MetadataProduced] = text;
                phrases.Add(new Phrase(chunk, group, new TimedItemWithMetadata(startTime, chunk.StartTime + group.Max(w => w.EndTime), metadata)));
            }
            return phrases;
        }

        /// <summary>
        /// The subtitles (without the end padding), from the words of these phrases (in this order: the words of a phrase stay together): a new
        /// subtitle after a pause (MaxGap), after the end of a sentence (EndOfSentenceMinGap), when it would be too long (MaxDuration), when the
        /// speaker changes (Diarization). The subtitles that match TranscriptionToIgnorePatterns are left out. With the speaker of each one.
        /// </summary>
        private List<(TimedItemWithMetadata Item, string Speaker)> BuildLines(SubtitleGeneratorContext context, List<Phrase> phrases, bool logIgnored)
        {
            var lines = new List<List<(CachedChunk Chunk, AIEngineAPIGoogle.TranscribedWordInfo Word)>>();
            List<(CachedChunk Chunk, AIEngineAPIGoogle.TranscribedWordInfo Word)> current = null;
            TimeSpan Start((CachedChunk Chunk, AIEngineAPIGoogle.TranscribedWordInfo Word) w) => w.Chunk.StartTime + w.Word.StartTime;
            TimeSpan End((CachedChunk Chunk, AIEngineAPIGoogle.TranscribedWordInfo Word) w) => w.Chunk.StartTime + w.Word.EndTime;
            foreach (var word in phrases.SelectMany(p => p.Words.Select(w => (p.Chunk, w))))
            {
                if (current != null)
                {
                    var previous = current[^1];
                    var gap = Start(word) - End(previous);
                    var gapTooLong = this.MaxGap.HasValue && gap > this.MaxGap.Value;
                    var tooLong = this.MaxDuration.HasValue && End(word) - current.Min(Start) > this.MaxDuration.Value;
                    var previousText = (previous.Word.Text ?? "").Trim();
                    var endOfSentence = this.EndOfSentenceMinGap.HasValue
                        && previousText.Length > 0
                        && EndOfSentence.Contains(previousText[^1])
                        && gap >= this.EndOfSentenceMinGap.Value;
                    var otherSpeaker = this.Diarization && word.w.Speaker != previous.Word.Speaker;
                    // (a phrase said at the same time as the previous one: another speaker, or the same line twice, see SameLineMaxStartDifference)
                    var overlapsPrevious = gap < -OverlapRecoveryMargin;
                    if (gapTooLong || endOfSentence || tooLong || otherSpeaker || overlapsPrevious)
                        current = null;
                }
                if (current == null)
                {
                    current = new List<(CachedChunk Chunk, AIEngineAPIGoogle.TranscribedWordInfo Word)>();
                    lines.Add(current);
                }
                current.Add(word);
            }

            var result = new List<(TimedItemWithMetadata Item, string Speaker)>();
            foreach (var line in lines)
            {
                var text = ExtractText(line);
                if (text.Length == 0)
                    continue;
                var startTime = line.Min(Start);
                var pattern = this.TranscriptionToIgnorePatterns?.FirstOrDefault(p => p.Regex.IsMatch(text));
                if (pattern != null)
                {
                    if (logIgnored)
                        context.WriteLog($"Ignoring '{pattern.Name}' transcription at [{context.WIP.TimelineMap.ConvertToPartSpecificFileIndexAndTime(startTime)}]: {text}");
                    continue;
                }
                var speaker = line[0].Word.Speaker;
                var metadata = new MetadataCollection();
                metadata[this.MetadataProduced] = text;
                if (this.Diarization && speaker != null && !string.IsNullOrWhiteSpace(this.SpeakerMetadata))
                    metadata[this.SpeakerMetadata] = speaker;
                result.Add((new TimedItemWithMetadata(startTime, line.Max(End), metadata), speaker));
            }
            return result;
        }

        /// <summary>
        /// The text of these words, as in the text of the answers (with their spaces / punctuation): the words that follow each other in the same
        /// answer are taken together from its text, the parts of different answers are put one after the other (a space between two letters /
        /// digits of the latin alphabet).
        /// </summary>
        private static string ExtractText(List<(CachedChunk Chunk, AIEngineAPIGoogle.TranscribedWordInfo Word)> words)
        {
            var result = new StringBuilder();
            void Append(string part)
            {
                part = part.Trim();
                if (part.Length == 0)
                    return;
                if (result.Length > 0 && result[^1] < 128 && char.IsLetterOrDigit(result[^1]) && part[0] < 128 && char.IsLetterOrDigit(part[0]))
                    result.Append(' ');
                result.Append(part);
            }
            for (var index = 0; index < words.Count;)
            {
                var (chunk, first) = words[index];
                var bytes = rs_textBytes.GetValue(chunk, c => Encoding.UTF8.GetBytes(c.Text ?? string.Empty));
                // (the words of the same answer, one after the other in its text: at most a few bytes between them, ex. a space)
                var last = index;
                while (last + 1 < words.Count
                    && words[last + 1].Chunk == chunk
                    && words[last].Word.EndIndex is int previousEnd
                    && words[last + 1].Word.StartIndex is int nextStart
                    && nextStart >= previousEnd
                    && nextStart - previousEnd <= 3)
                {
                    last++;
                }
                var lastWord = words[last].Word;
                if (first.StartIndex is int startIndex && lastWord.EndIndex is int endIndex && endIndex <= bytes.Length && startIndex < endIndex)
                    Append(Encoding.UTF8.GetString(bytes, startIndex, endIndex - startIndex));
                else
                    for (var k = index; k <= last; k++)
                        Append(words[k].Word.Text ?? string.Empty);
                index = last + 1;
            }
            return result.ToString();
        }
    }
}
