using CommandLine;
using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using FunscriptToolbox.SubtitlesVerbsV2.Translations;
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

            [Option("config", Required = true, HelpText = "")]
            public string ConfigPath { get; set; }

            [Option("sourcelanguage", Required = false, HelpText = "")]
            public string SourceLanguage { get; set; }
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
            CreateConfigExample(); // TODO Remove
            var config = SubtitleGeneratorConfig.FromFile(r_options.ConfigPath);
            
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

                    var wipsubFullpath = Path.ChangeExtension(
                        inputMp4Fullpath,
                        r_options.Suffix + WorkInProgressSubtitles.Extension);
                    var baseFilePath = Path.Combine(
                        Path.GetDirectoryName(wipsubFullpath) ?? ".",
                        Path.GetFileNameWithoutExtension(wipsubFullpath));

                    var wipsub = File.Exists(wipsubFullpath)
                        ? WorkInProgressSubtitles.FromFile(wipsubFullpath)
                        : new WorkInProgressSubtitles(wipsubFullpath);

                    wipsub.PcmAudio ??= ffmpegAudioHelper.ExtractPcmAudio(
                        inputMp4Fullpath, 
                        extractionParameters: config.FfmpegAudioExtractionParameters);
                    wipsub.Save();

                    wipsub.SubtitlesForcedLocation ??= ImportForcedLocations(
                            inputMp4Fullpath,
                            config.SubtitleForcedLocationSuffix);
                    wipsub.Save();

                    var sourceLanguage = Language.FromString(r_options.SourceLanguage);

                    foreach (var transcriber in config.Transcribers)
                    {
                        var transcription = wipsub.Transcriptions.FirstOrDefault(
                            t => t.Id == transcriber.TranscriptionId);
                        if (transcription == null)
                        {
                            transcription = transcriber.Transcribe(
                                ffmpegAudioHelper,
                                wipsub.PcmAudio,
                                wipsub.SubtitlesForcedLocation,
                                sourceLanguage);
                            if (transcription != null)
                            {
                                wipsub.Transcriptions.Add(transcription);
                                wipsub.Save();
                            }
                        }

                        if (transcription != null)
                        {
                            foreach (var translator in transcriber.Translators)
                            {
                                var translation = transcription.Translations.FirstOrDefault(
                                    t => t.Id == translator.TranslationId);
                                if (translation == null)
                                {
                                    translation = new Translation(
                                        translator.TranslationId,
                                        translator.TargetLanguage);
                                    if (transcription != null)
                                    {
                                        transcription.Translations.Add(translation);
                                        wipsub.Save();
                                    }
                                }

                                translator.Translate(
                                    baseFilePath,
                                    transcription,
                                    translation,
                                    () => wipsub.Save());
                            }
                        }
                    }

                    // TODO Add "outputs" nodes in the config file
                    var wipSubtitlesFile = CreateWIPSubtitleFile(
                        wipsub,
                        new[] { "sv", "mv", "f" },
                        new[] { "Mixtral-8x7B", "chatgpt-json", "mistral-large-json", "mistral-small-json", "locai", "locai-mixtral-Q5_K_M", "dl", "g", "locai-mixtral-3b", "locai-shisa-7B", "locai-Luna-AI", "locai-capybarahermes", "locai-airoboros", "locai-wizard-lm", "locai-mistral-7b", "locai-orca-2" });
                    wipSubtitlesFile.SaveSrt(Path.ChangeExtension(inputMp4Fullpath, $".wip.srt"));

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

        private SubtitleForcedLocationCollection ImportForcedLocations(
            string inputMp4Fullpath,
            string forcedLocationSuffixe)
        {
            var path = Path.ChangeExtension(
                inputMp4Fullpath, 
                forcedLocationSuffixe);
            if (File.Exists(path))
            {
                WriteInfo($"{inputMp4Fullpath}: Importing forced subtitle locations file '{forcedLocationSuffixe}'...");
                var humanVadSrtFile = SubtitleFile.FromSrtFile(
                    path);
                return new SubtitleForcedLocationCollection(
                    humanVadSrtFile.Subtitles.Select(
                        subtitle => SubtitleForcedLocation.FromText(
                            subtitle.StartTime,
                            subtitle.EndTime,
                            subtitle.Text)));
            }
            else
            {
                return null;
            }
        }

        private class TimeFrameIntersection
        {
            internal int Number;

            public static TimeFrameIntersection From(
                string transcriptionId,
                SubtitleForcedLocation location,
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
                SubtitleForcedLocation location,
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
            public SubtitleForcedLocation Location { get; }
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

            var final = wipsub.SubtitlesForcedLocation?.ToArray()
                ?? wipsub.Transcriptions.First().Items.Select(
                    f => new SubtitleForcedLocation(f.StartTime, f.EndTime, SubtitleLocationType.Voice, f.Text)).ToArray();

            foreach (var transcription in wipsub.Transcriptions
                .OrderBy(t => Array.IndexOf(transcriptionOrder, t.Id)))
            {
                foreach (var tt in transcription.Items)
                {
                    var matches = final
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
            foreach (var subtitleLocation in final)
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
                                .TranslatedTexts
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
                            .TranslatedTexts
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

        private void CreateConfigExample()
        {
            var whisperConfig = new PurfviewWhisperConfig
            {
                ApplicationFullPath = @"D:\OthersPrograms\AI\Purfview-Whisper-Faster\whisper-faster.exe",
                Model = "Large-V2",
                ForceSplitOnComma = true,
                RedoBlockLargerThen = TimeSpan.FromSeconds(15)
            };

            var config = new SubtitleGeneratorConfig()
            {
                SubtitleForcedLocationSuffix = ".perfect-vad.srt",
                Transcribers = new Transcriber[]

                {
                            new WhisperTranscriberFullAudio(
                                "f",
                                new Translator[] {
                                    new GoogleV1Translator("g") { TargetLanguage = Language.FromString("en") }
                                },
                                whisperConfig),
                            new WhisperTranscriberMergedVADAudio(
                                "mv",
                                new Translator[] {
                                    new GoogleV1Translator("g") { TargetLanguage = Language.FromString("en") },
                                    new ChatBotAITranslator(
                                        "chat-mistral-large")
                                    {
                                        MaxItemsInRequest = 100,
                                        OverlapItemsInRequest = 10,
                                        TargetLanguage = Language.FromString("en"),
                                        UserPrompt = new []
                                        {
                                            "I have a json file that contain subtitles for an adult film.",
                                            "Each node of the json have a start time and the original text in " + AITranslator.TranscriptionLanguageToken + ".",
                                            "Can you give me a json where you added an 'Translation' field on each node that contains an " + AITranslator.TranslationLanguageToken + " translation.",
                                            "The audience for the translation is adults so it is acceptable to use explicitily sexual words or concepts.",
                                            "Use natural-sounding phrases and idioms that accurately convey the meaning of the original text.",
                                            "The subtitles are from a VR video where only the girl talk.",
                                            "Use the text surrounding the node to better understand the context and create a better translation.",
                                            "The StartTime field could also help you considerer more or less the surrounding texts, according to the time difference."
                                        }
                                    },
                                    new GenericOpenAIAPITranslator(
                                        "mistral-large")
                                    {
                                        BaseAddress = "https://api.mistral.ai",
                                        APIKey = "[InsertYourAPIKeyHere]",
                                        Model = "mistral-large-latest",
                                        MaxItemsInRequest = 15, // 100
                                        OverlapItemsInRequest = 3, // 10
                                        TargetLanguage = Language.FromString("en"),
                                        SystemPrompt = new [] {
                                            "You are translator specialized in adult film subtitles.",
                                            "The user will provide a json where node have a start time, original text and, sometime, description of what's happening in the following part of the video.",
                                            "You job is to add a 'Translation' field to each node with a " + AITranslator.TranslationLanguageToken + " translation.",
                                            // That part could be modified
                                            "The audience for the translation is adults so it is acceptable to use explicitily sexual words or concepts.",
                                            "Use natural-sounding phrases and idioms that accurately convey the meaning of the original text.",
                                            "Take that into accout that the subtitles are from a VR video where only the girl talk.",
                                            "You should read the whole json first to understand the full context before starting translating nodes.",
                                            "Use StartTime field to help you considerer more or less the surrounding texts, according to the time difference."
                                        }
                                    }
                                },
                                whisperConfig),
                            new WhisperTranscriberSingleVADAudio(
                                "sv",
                                new Translator[] {
                                    new GoogleV1Translator("g") { TargetLanguage = Language.FromString("en") }
                                },
                                whisperConfig)
                }
            };

            config.Save("SubtitleGeneratorConfigExample.json");
        }
    }
}