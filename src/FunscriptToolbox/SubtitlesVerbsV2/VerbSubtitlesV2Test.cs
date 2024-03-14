using AudioSynchronization;
using CommandLine;
using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbsV2.Transcription;
using FunscriptToolbox.SubtitlesVerbsV2.Translation;
using log4net;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    [JsonObject(IsReference = false)]
    class VerbSubtitlesV2Video2Test : VerbSubtitlesV2
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("subtitlesv2.video2test", aliases: new[] { "subv2.vid2test" }, HelpText = "Extract an dummy .srt from video, using Voice Activity Detection")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = true, HelpText = ".mp4 files")]
            public IEnumerable<string> Input { get; set; }

            [Option('s', "suffix", Required = false, HelpText = "Suffix for the files produced", Default = "")]
            public string Suffix { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }

            [Option('p', "extractionparameters", Required = false, HelpText = "Added parameters to pass to ffmpeg when extracting wav")]
            public string ExtractionParameters { get; set; }

            [Option('v', "skipvad", Required = false, HelpText = "Don't use Voice Activity Detection, create an empty .vad file instead", Default = false)]
            public bool SkipVAD { get; set; }

            [Option("silerovad", Required = false, HelpText = "", Default = false)]
            public bool SileroVAD { get; set; }

            [Option("importvad", Required = false, HelpText = "")]
            public string ImportVAD { get; set; }

            [Option("transcribe", Required = false, HelpText = "")]
            public string Transcribe { get; set; }

            [Option("sourcelanguage", Required = false, HelpText = "")]
            public string SourceLanguage { get; set; }

            [Option("targetlanguage", Required = false, HelpText = "", Default = "en")]
            public string TargetLanguage { get; set; }
        }

        private readonly Options r_options;

        public VerbSubtitlesV2Video2Test(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            return Task.Run<int>(() => ExecuteAsync()).Result;
        }

        public async Task<int> ExecuteAsync()
        {
            foreach (var inputMp4Fullpath in r_options
                .Input
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive))
                .Distinct()
                .OrderBy(f => f))
            {
                try
                {
                    var watch = Stopwatch.StartNew();
                    var ffmpegAudioHelper = new FfmpegAudioHelper();
                    var whisperHelper = new WhisperHelper(ffmpegAudioHelper, @"D:\OthersPrograms\AI\Purfview-Whisper-Faster\whisper-faster.exe");

                    var wipsubFullpath = Path.ChangeExtension(
                        inputMp4Fullpath,
                        r_options.Suffix + WorkInProgressSubtitles.Extension);

                    var wipsub = File.Exists(wipsubFullpath)
                        ? WorkInProgressSubtitles.FromFile(wipsubFullpath)
                        : new WorkInProgressSubtitles(wipsubFullpath);

                    wipsub.PcmAudio ??= ffmpegAudioHelper.ExtractPcmAudio(inputMp4Fullpath, extractionParameters: r_options.ExtractionParameters);
                    wipsub.Save();

                    wipsub.FinalSubtitlesLocation ??= ImportVad(inputMp4Fullpath);
                    wipsub.Save();

                    // if (DoFull)
                    wipsub.Transcriptions.AddIfMissing(
                        "f",
                        (id) => new FullTranscription(
                            id,
                            whisperHelper.TranscribeAudio(
                                new[] { wipsub.PcmAudio },
                                language: r_options.SourceLanguage)));
                    wipsub.Save();

                    if (wipsub.FinalSubtitlesLocation != null)
                    {
                        wipsub.Transcriptions.AddIfMissing(
                            "sv",
                            (id) => new FullTranscription(
                                    id,
                                    whisperHelper.TranscribeAudio(
                                        wipsub
                                        .FinalSubtitlesLocation
                                        .Where(f => f.Type == SubtitleLocationType.Voice)
                                        .Select(
                                            vad => wipsub.PcmAudio.ExtractSnippet(vad.StartTime, vad.EndTime))
                                        .ToArray(),
                                        language: r_options.SourceLanguage)));
                        wipsub.Save();

                        wipsub.Transcriptions.AddIfMissing(
                            "mv",
                            (id) => CreateTranscriptionFromMergedAudio(
                                id,
                                whisperHelper,
                                wipsub.PcmAudio,
                                wipsub.FinalSubtitlesLocation.Where(f => f.Type == SubtitleLocationType.Voice),
                                language: r_options.SourceLanguage));
                        wipsub.Save();
                    }

                    var googleTranslator = new GoogleTranslate();
                    foreach (var transcription in wipsub.Transcriptions)
                    {
                        googleTranslator.Translate(
                            "g",
                            transcription,
                            r_options.SourceLanguage,
                            r_options.TargetLanguage,
                            () => wipsub.Save());
                    }

                    wipsub.Save();

                    if (wipsub.FinalSubtitlesLocation != null)
                    {
                        foreach (var transcription in wipsub.Transcriptions.Where(t => t.Id == "mv"))
                        {
                            var dlId = "dl";
                            var dlFile = Path.ChangeExtension(inputMp4Fullpath, $"{transcription.Id}.DL.txt");
                            var dlResultFile = Path.ChangeExtension(inputMp4Fullpath, $"{transcription.Id}.DL.result.txt");
                            if (File.Exists(dlResultFile) && File.Exists(dlFile))
                            {
                                var dlResult = File.ReadAllLines(dlResultFile);
                                int index = 0;

                                foreach (var tt in transcription.Items)
                                {
                                    var alreadyTranslated = tt.Translations.FirstOrDefault(t => t.Id == dlId)?.Text;
                                    if (alreadyTranslated == null)
                                    {
                                        tt.Translations.Add(new TranslatedText(dlId, dlResult[index]));
                                    }

                                    index++;
                                }
                                wipsub.Save();
                            }
                            else
                            {
                                using (var writerDL = File.CreateText(dlFile))
                                {
                                    int index = 0;

                                    foreach (var tt in transcription.Items)
                                    {
                                        index++;
                                        writerDL.WriteLine($"{tt.Text}");
                                    }
                                }
                            }
                        }

                        var localAITranslator = new AITranslator();
                        localAITranslator.Translate(
                            "locai-mixtral-Q5_K_M",
                            wipsub.Transcriptions.FirstOrDefault(t => t.Id == "mv"),
                            r_options.SourceLanguage,
                            r_options.TargetLanguage,
                            () => wipsub.Save());
                        wipsub.Save();

                        var wipSubtitlesFile = CreateWIPSubtitleFile(
                            wipsub,
                            new[] { "sv", "mv", "f" },
                            new[] { "locai", "locai-mixtral-Q5_K_M", "dl", "g", "locai-mixtral-3b", "locai-shisa-7B", "locai-Luna-AI", "locai-capybarahermes", "locai-airoboros", "locai-wizard-lm", "locai-mistral-7b", "locai-orca-2" });
                        wipSubtitlesFile.SaveSrt(Path.ChangeExtension(inputMp4Fullpath, $".wip.srt"));
                    }

                    // TODO:
                    // Create subtitle file with all the infos
                    // Create import/export file for chatgpt/llm
                    // Translate with DeepL

                    WriteInfo($"{inputMp4Fullpath}: Finished in {watch.Elapsed}.");
                    WriteInfo();
                }
                catch (Exception ex)
                {
                    WriteError($"{inputMp4Fullpath}: Exception occured: {ex}");
                    WriteInfo();
                }
            }

            return base.NbErrors;
        }

        private FinalSubtitleLocationCollection ImportVad(
            string inputMp4Fullpath)
        {
            var vadFilePath = Path.ChangeExtension(
                inputMp4Fullpath,
                r_options.ImportVAD);
            if (File.Exists(vadFilePath))
            {
                WriteInfo($"{inputMp4Fullpath}: Importing human VAD file '{r_options.ImportVAD}'...");
                var humanVadSrtFile = SubtitleFile.FromSrtFile(
                    vadFilePath);
                return new FinalSubtitleLocationCollection(
                    humanVadSrtFile.Subtitles.Select(
                        subtitle => FinalSubtitleLocation.FromText(
                            subtitle.StartTime,
                            subtitle.EndTime,
                            subtitle.Text)));
            }
            else
            {
                return null;
            }
        }

        private FullTranscription CreateTranscriptionFromMergedAudio(
            string id,
            WhisperHelper whisperHelper,
            PcmAudio pcmAudio,
            IEnumerable<FinalSubtitleLocation> audioDetections,
            string language)
        {
            var silenceGapSamples = pcmAudio.GetSilenceAudio(
                TimeSpan.FromSeconds(0.3));

            var currentDuration = TimeSpan.Zero;
            var audioOffsets = new List<AudioOffset>();
            var mergedAudio = new MemoryStream();
            foreach (var vad in audioDetections)
            {
                var partAudio = pcmAudio.ExtractSnippet(vad.StartTime, vad.EndTime);

                audioOffsets.Add(
                    new AudioOffset(
                        currentDuration,
                        currentDuration + partAudio.Duration + silenceGapSamples.Duration,
                        vad.StartTime - currentDuration));

                mergedAudio.Write(silenceGapSamples.Data, 0, silenceGapSamples.Data.Length / 2);
                mergedAudio.Write(partAudio.Data, 0, partAudio.Data.Length);
                mergedAudio.Write(silenceGapSamples.Data, 0, silenceGapSamples.Data.Length / 2);
                currentDuration += partAudio.Duration;
                currentDuration += silenceGapSamples.Duration;
            }

            var offsetCollection = new AudioOffsetCollection(audioOffsets);

            var mergedPcm = new PcmAudio(pcmAudio.SamplingRate, mergedAudio.ToArray());

            var transcribedTexts = new List<TranscribedText>();
            foreach (var original in whisperHelper.TranscribeAudio(
                                new[] { mergedPcm },
                                language: language))
            {
                var newStartTime = offsetCollection.TransformPosition(original.StartTime);
                var newEndTime = offsetCollection.TransformPosition(original.EndTime);
                if (newStartTime == null || newEndTime == null)
                {
                    throw new Exception("BUG");
                }
                transcribedTexts.Add(
                    new TranscribedText(
                        newStartTime.Value,
                        newEndTime.Value,
                        original.Text,
                        original.NoSpeechProbability,
                        original.Words));
            }

            return new FullTranscription(id, transcribedTexts);
        }

        private class TimeFrameIntersection
        {
            internal int Number;

            public static TimeFrameIntersection From(
                string transcriptionId,
                FinalSubtitleLocation location,
                TranscribedText tt)
            {
                var intersectionStartTime = location.StartTime > tt.StartTime ? location.StartTime : tt.StartTime;
                var intersectionEndTime = location.EndTime < tt.EndTime ? location.EndTime : tt.EndTime;
                return (intersectionStartTime < intersectionEndTime)
                    ? new TimeFrameIntersection(
                        transcriptionId,
                        intersectionStartTime,
                        intersectionEndTime,
                        location,
                        tt)
                    : null;
            }

            public TimeFrameIntersection(
                string transcriptionId,
                TimeSpan startTime,
                TimeSpan endTime,
                FinalSubtitleLocation location,
                TranscribedText tt)
            {
                TranscriptionId = transcriptionId;
                StartTime = startTime;
                EndTime = endTime;
                Location = location;
                TranscribedText = tt;
                if (location != null)
                {
                    MatchPercentage = (int)(100 * (Math.Min(endTime.TotalMilliseconds, location.EndTime.TotalMilliseconds)
                        - Math.Max(startTime.TotalMilliseconds, location.StartTime.TotalMilliseconds)) / location.Duration.TotalMilliseconds);
                }
            }

            public string TranscriptionId { get; }
            public TimeSpan StartTime { get; }
            public TimeSpan EndTime { get; }
            public TimeSpan Duration => EndTime - StartTime;
            public FinalSubtitleLocation Location { get; }
            public TranscribedText TranscribedText { get; }
            public int MatchPercentage { get; }
            public int Index { get; internal set; }
            public int PercentageTime { get; internal set; }
        }

        private SubtitleFile CreateWIPSubtitleFile(
            WorkInProgressSubtitles wipsub,
            string[] transcriptionOrder,
            string[] translationOrder)
        {
            var wipSubtitleFile = new SubtitleFile();

            var intersections = new List<TimeFrameIntersection>();
            var unmatchedTranscribedTexts = new List<TimeFrameIntersection>();

            foreach (var transcription in wipsub.Transcriptions
                .OrderBy(t => Array.IndexOf(transcriptionOrder, t.Id)))
            {
                foreach (var tt in transcription.Items)
                {
                    var matches = wipsub
                        .FinalSubtitlesLocation
                        .Select(location => TimeFrameIntersection.From(transcription.Id, location, tt))
                        .Where(f => f != null)
                        .ToArray();
                    if (matches.Length == 0)
                    {
                        unmatchedTranscribedTexts.Add(
                            new TimeFrameIntersection(
                                transcription.Id,
                                tt.StartTime,
                                tt.EndTime,
                                null,
                                tt));
                    }
                    else
                    {
                        var totalTimeMatched = matches.Select(m => m.Duration.TotalMilliseconds).Sum();

                        var index = 1;
                        foreach (var match in matches)
                        {
                            match.Index = index++;
                            match.Number = matches.Length;
                            match.PercentageTime = (int)(100 * match.Duration.TotalMilliseconds / totalTimeMatched);
                            intersections.Add(match);
                        }
                    }
                }
            }

            TimeSpan? previousEnd = null;
            foreach (var subtitleLocation in wipsub.FinalSubtitlesLocation)
            {
                if (subtitleLocation.Type == SubtitleLocationType.Screengrab)
                {
                    wipSubtitleFile.Subtitles.Add(
                        new Subtitle(
                            subtitleLocation.StartTime,
                            subtitleLocation.EndTime,
                            $"Screengrab: {subtitleLocation.Text}"));
                }
                else if (subtitleLocation.Type == SubtitleLocationType.Context)
                {
                }
                else
                {
                    if (previousEnd != null)
                    {
                        foreach (var utt in unmatchedTranscribedTexts
                            .Where(f => f.StartTime < subtitleLocation.StartTime)
                            .ToArray())
                        {
                            var builder2 = new StringBuilder();

                            builder2.AppendLine("** OUT OF SCOPE **");
                            builder2.AppendLine($"[{utt.TranscriptionId}] {utt.TranscribedText.Text}");
                            foreach (var translation in utt
                                .TranscribedText
                                .Translations
                                .OrderBy(f => Array.IndexOf(translationOrder, f.Id)))
                            {
                                builder2.AppendLine($"   [{translation.Id}] {translation.Text}");
                            }

                            wipSubtitleFile.Subtitles.Add(
                                new Subtitle(
                                    utt.StartTime,
                                    utt.EndTime,
                                    builder2.ToString()));

                            unmatchedTranscribedTexts.Remove(utt);
                        }
                    }

                    var builder = new StringBuilder();
                    foreach (var item in intersections.Where(f => f.Location == subtitleLocation))
                    {
                        if (builder.Length > 0)
                        {
                            builder.AppendLine("--------------");
                        }

                        var xyz = item.Number > 1 ? $"{item.Index}/{item.Number}," : string.Empty;
                        builder.AppendLine($"[{item.TranscriptionId}] {item.TranscribedText.Text} ({xyz}{item.PercentageTime}%, {item.MatchPercentage}%)");
                        foreach (var translation in item
                            .TranscribedText
                            .Translations
                            .OrderBy(f => Array.IndexOf(translationOrder, f.Id)))
                        {
                            builder.AppendLine($"   [{translation.Id}] {translation.Text}");
                        }
                    }

                    if (builder.Length > 0)
                    {
                        builder.AppendLine("** NO TRANSCRIPTION FOUND **");
                    }
                    wipSubtitleFile.Subtitles.Add(
                        new Subtitle(
                            subtitleLocation.StartTime,
                            subtitleLocation.EndTime,
                            builder.ToString()));
                    previousEnd = subtitleLocation.EndTime;
                }
            }

            wipSubtitleFile.ExpandTiming(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5));
            return wipSubtitleFile;
        }
    }
}