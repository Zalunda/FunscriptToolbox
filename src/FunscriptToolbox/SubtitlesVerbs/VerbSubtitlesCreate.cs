using CommandLine;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using FunscriptToolbox.SubtitlesVerbs.Outputs;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using FunscriptToolbox.SubtitlesVerbs.Translations;
using log4net;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FunscriptToolbox.SubtitlesVerbs
{
    [JsonObject(IsReference = false)]
    class VerbSubtitlesCreate : Verb
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("subtitles.create", aliases: new[] { "sub.create" }, HelpText = "Create a subtitle from a video (transcribe / translate).")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = true, HelpText = ".mp4 files")]
            public IEnumerable<string> Input { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }

            [Option('s', "suffix", Required = false, HelpText = "Suffix for the files produced", Default = "")]
            public string Suffix { get; set; }

            [Option("config", Required = true, HelpText = "")]
            public string ConfigPath { get; set; }

            [Option("sourcelanguage", Required = false, HelpText = "")]
            public string SourceLanguage { get; set; }

            [Option("removetranslations", Required = false, HelpText = "")]
            public string RemoveTranslations { get; set; }
        }

        private readonly Options r_options;

        public VerbSubtitlesCreate(Options options)
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
            var config = SubtitleGeneratorConfig.FromFile(
                r_options.ConfigPath);
            var privateConfig = SubtitleGeneratorPrivateConfig.FromFile(
                Path.ChangeExtension(r_options.ConfigPath, ".private.json"));
            var context = new SubtitleGeneratorContext(
                rs_log,
                r_options.Verbose,
                new FfmpegAudioHelper(),
                privateConfig);

            var errors = new List<string>();
            var userTodoList = new List<string>();
            foreach (var inputMp4Fullpath in r_options
                .Input
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive))
                .Distinct()
                .OrderBy(f => f))
            {
                var watchGlobal = Stopwatch.StartNew();

                var wipsubFullpath = Path.ChangeExtension(
                    inputMp4Fullpath,
                    r_options.Suffix + WorkInProgressSubtitles.Extension);
                var wipsub = File.Exists(wipsubFullpath)
                    ? WorkInProgressSubtitles.FromFile(wipsubFullpath)
                    : new WorkInProgressSubtitles(wipsubFullpath);

                context.ChangeCurrentFile(wipsub);

                UpdateWipSubFileIfNeeded(context, config);

                // 0. Remove translations 
                var translationsToRemove = r_options.RemoveTranslations?.Split(';').ToArray();
                if (translationsToRemove != null)
                {
                    foreach (var translationId in translationsToRemove)
                    {
                        var nbTranslatedTexts = 0;
                        foreach (var transcription in wipsub.Transcriptions) 
                        {
                            nbTranslatedTexts += transcription.RemoveTranslation(translationId);
                        }
                        context.WriteInfo($"Removed translation '{translationId}': {nbTranslatedTexts} translated texts removed.");
                        wipsub.Save();
                    }
                }

                try
                {

                    // 1. Importing Subtitle Forced Timings, if not already done.
                    var forcedTimingPath = Path.ChangeExtension(
                        inputMp4Fullpath,
                        config.SubtitleForcedTimingsParser.FileSuffix);
                    if (File.Exists(forcedTimingPath))
                    {
                        var newForcedTimings = config.SubtitleForcedTimingsParser.ParseFromFile(forcedTimingPath);
                        if (wipsub.SubtitlesForcedTiming != null && newForcedTimings.SequenceEqual(wipsub.SubtitlesForcedTiming))
                        {
                            context.WriteInfoAlreadyDone($"Subtitle forced timings have already been imported:");
                            context.WriteInfoAlreadyDone($"    Number of timings = {wipsub.SubtitlesForcedTiming.Count}");
                            context.WriteInfoAlreadyDone($"    Timings Duration = {TimeSpan.FromMilliseconds(wipsub.SubtitlesForcedTiming.Sum(f => f.Duration.TotalMilliseconds))}");
                            context.WriteInfoAlreadyDone();
                        }
                        else
                        {
                            var importOrUpdate = wipsub.SubtitlesForcedTiming == null ? "Importing" : "Updating";
                            context.WriteInfo($"{importOrUpdate} forced subtitle timings from '{Path.GetFileName(forcedTimingPath)}'...");

                            wipsub.SubtitlesForcedTiming = config.SubtitleForcedTimingsParser.ParseFromFile(forcedTimingPath);
                            wipsub.Save();

                            context.WriteInfo($"Finished:");
                            context.WriteInfo($"    Number of timings = {wipsub.SubtitlesForcedTiming.Count}");
                            context.WriteInfo($"    Timings Duration = {TimeSpan.FromMilliseconds(wipsub.SubtitlesForcedTiming.Sum(f => f.Duration.TotalMilliseconds))}");
                            context.WriteInfo();
                        }
                    }
                    else if (config.Outputs.Any(f => f.NeedSubtitleForcedTimings))
                    {
                        context.AddUserTodo($"Create the subtitle forced timings file '{Path.GetFileName(forcedTimingPath)}'.");
                    }

                    // 2. Extracting PcmAudio, if not already done.
                    var pcmFilePath = wipsub.OriginalFilePath + ".pcm";
                    if (wipsub.PcmAudio != null && File.Exists(pcmFilePath))
                    {
                        wipsub.PcmAudio.RegisterLoadPcmFunc(() => File.ReadAllBytes(pcmFilePath));
                        context.WriteInfoAlreadyDone($"PcmAudio has already been extracted:");
                        context.WriteInfoAlreadyDone($"    Audio Duration = {wipsub.PcmAudio.Duration}");
                        context.WriteInfoAlreadyDone();
                    }
                    else
                    {
                        context.WriteInfo($"Extracting PCM audio from '{Path.GetFileName(inputMp4Fullpath)}'...");

                        var watchPcmAudio = Stopwatch.StartNew();
                        wipsub.PcmAudio = config.AudioExtractor.ExtractPcmAudio(context, inputMp4Fullpath);
                        wipsub.Save();
                        File.WriteAllBytes(pcmFilePath, wipsub.PcmAudio.Data);
                   
                        context.WriteInfo($"Finished in {watchPcmAudio.Elapsed}:");
                        context.WriteInfo($"    Audio Duration = {wipsub.PcmAudio.Duration}");
                        context.WriteInfo();
                    }

                    // 3. Transcribing the audio file, if not already done.
                    var sourceLanguage = Language.FromString(r_options.SourceLanguage);
                    foreach (var transcriber in config.Transcribers
                        ?.Where(t => t.Enabled)
                        ?? Array.Empty<Transcriber>())
                    {
                        var transcription = wipsub.Transcriptions.FirstOrDefault(
                            t => t.Id == transcriber.TranscriptionId);
                        if (transcription != null)
                        {
                            context.WriteInfoAlreadyDone($"Transcription '{transcriber.TranscriptionId}' have already been done:");
                            context.WriteInfoAlreadyDone($"    Number of subtitles = {transcription.Items.Length}");
                            context.WriteInfoAlreadyDone($"    Total subtitles duration = {transcription.Items.Sum(f => f.Duration)}");
                            if (transcriber.Language == null)
                            {
                                context.WriteInfoAlreadyDone($"    Detected Language = {transcription.Language.LongName}");
                            }
                            foreach (var line in GetTranscriptionAnalysis(context, transcription))
                            {
                                context.WriteInfoAlreadyDone(line);
                            }
                            context.WriteInfoAlreadyDone();
                        }
                        else if (!transcriber.IsPrerequisitesMet(context, out var reason))
                        {
                            context.WriteInfo($"Transcription '{transcriber.TranscriptionId}' can't be done yet: {reason}");
                            context.WriteInfo();
                        }
                        else
                        {
                            try
                            {
                                var watch = Stopwatch.StartNew();
                                context.WriteInfo($"Transcribing '{transcriber.TranscriptionId}'...");
                                transcription = transcriber.Transcribe(
                                    context,
                                    wipsub.PcmAudio,
                                    sourceLanguage);

                                context.WriteInfo($"Finished in {watch.Elapsed}:");
                                context.WriteInfo($"    Number of subtitles = {transcription.Items.Length}");
                                context.WriteInfo($"    Total subtitles duration = {transcription.Items.Sum(f => f.Duration)}");
                                if (transcriber.Language == null)
                                {
                                    context.WriteInfo($"    Detected Language = {transcription.Language.LongName}");
                                }
                                foreach (var line in GetTranscriptionAnalysis(context, transcription))
                                {
                                    context.WriteInfo(line);
                                }
                                context.WriteInfo();

                                wipsub.Transcriptions.Add(transcription);
                                wipsub.Save();
                            }
                            catch (TranscriberNotReadyException ex)
                            {
                                context.WriteInfo($"Transcription '{transcriber.TranscriptionId}' can't be done yet: {ex.Reason}");
                                context.WriteInfo();
                                foreach (var userTodo in ex.UserTodos)
                                {
                                    context.AddUserTodo(userTodo);
                                }
                            }
                            catch (Exception ex)
                            {
                                context.WriteError($"An error occured while transcribing '{transcriber.TranscriptionId}':\n{ex.Message}");
                            }
                        }
                    }

                    // 4. Translating the transcribed text, if not already done.
                    foreach (var transcriber in config.Transcribers
                        ?.Where(t => t.Enabled)
                        ?? Array.Empty<Transcriber>())
                    {
                        var transcription = wipsub.Transcriptions.FirstOrDefault(
                            t => t.Id == transcriber.TranscriptionId);
                        if (transcription != null)
                        {
                            foreach (var translator in transcriber.Translators
                                ?.Where(t => t.Enabled)
                                ?? Array.Empty<Translator>())
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

                                if (translator.IsFinished(transcription, translation))
                                {
                                    context.WriteInfoAlreadyDone($"Translation '{transcription.Id}/{translation.Id}' have already been done.");
                                    context.WriteInfoAlreadyDone();
                                }
                                else if (!translator.IsReadyToStart(transcription, out var reason))
                                {
                                    context.WriteInfoAlreadyDone($"Translation '{transcription.Id}/{translation.Id}' cannot start yet because {reason}");
                                    context.WriteInfoAlreadyDone();
                                }
                                else
                                {
                                    try
                                    {
                                        var watch = Stopwatch.StartNew();
                                        context.WriteInfo($"Translating '{transcription.Id}/{translation.Id}'...");
                                        translator.Translate(
                                            context,
                                            transcription,
                                            translation);
                                        wipsub.Save();

                                        context.WriteInfo($"Finished in {watch.Elapsed}.");
                                        context.WriteInfo();
                                    }
                                    catch (Exception ex)
                                    {
                                        context.WriteError($"An error occured while translating '{transcription.Id}/{translation.Id}':\n{ex.Message}");
                                    }
                                }
                            }
                        }
                    }

                    // 5. Creating user defined outputs
                    foreach (var output in config.Outputs
                        ?.Where(o => o.Enabled) 
                        ?? Array.Empty<SubtitleOutput>())
                    {
                        if (!output.IsPrerequisitesMet(context, out var reason))
                        {
                            context.WriteInfo($"Output '{output.Description}' can't be done yet: {reason}");
                            context.WriteInfo();
                        }
                        else
                        {
                            context.WriteInfo($"Creating Output '{output.Description}'...");
                            output.CreateOutput(context);
                            context.WriteInfo($"Finished.");
                            context.WriteInfo();
                        }
                    }

                    context.WriteInfo($"Finished in {watchGlobal.Elapsed}.");
                    context.WriteInfo();
                }
                catch (Exception ex)
                {
                    context.WriteError($"Unexpected exception occured: {ex}");
                }
            }

            context.ForgetCurrentFile();

            // Write final report
            context.WriteInfo();
            if (context.Errors.Count > 0)
            {
                context.WriteInfo($"The following errors occured during the process:");
                var index = 1;
                foreach (var error in context.Errors)
                {
                    context.WriteNumeredPoint(index++, error, ConsoleColor.Red);
                }
                context.WriteInfo();
            }

            context.WriteInfo();
            if (context.UserTodoList.Count > 0)
            {
                context.WriteInfo($"You have the following task to do:");
                var index = 1;
                foreach (var usertodo in context.UserTodoList)
                {
                    context.WriteNumeredPoint(index++, usertodo, ConsoleColor.Green);
                }
                context.WriteInfo();
            }
            return base.NbErrors;
        }

        private void UpdateWipSubFileIfNeeded(SubtitleGeneratorContext context, SubtitleGeneratorConfig config)
        {
            switch (context.CurrentWipsub.FormatVersion)
            {
                case "1.0":
                    context.WriteInfo($"Updating WIPSub file format from {context.CurrentWipsub.FormatVersion} to {WorkInProgressSubtitles.CURRENT_FORMAT_VERSION}...");
                    context.CurrentWipsub.Save(Path.ChangeExtension(context.CurrentWipsub.OriginalFilePath, ".wipsubtitles-1.0"));
                    context.CurrentWipsub.UpdateFormatVersion();

                    foreach (var transcriber in config.Transcribers.OfType<TranscriberMergedVADAudio>())
                    {
                        foreach (var transcription in context.CurrentWipsub.Transcriptions
                            .Where(t => t.Id == transcriber.TranscriptionId))
                        {
                            var oldId = transcription.Id;
                            var newId = transcription.Id + "-1.0";
                            context.WriteInfo($"   Renaming transcription '{oldId}' to '{newId}', and updating word timings.");
                            transcription.Rename(newId);

                            foreach (var fullpath in Directory.GetFiles(
                                PathExtension.SafeGetDirectoryName(context.CurrentBaseFilePath),
                                "*.*"))
                            {
                                var filename = Path.GetFileName(fullpath);
                                if (Regex.IsMatch(
                                    filename,
                                    $"^" + Regex.Escape($"{Path.GetFileName(context.CurrentBaseFilePath)}.TODO-{oldId}-") + $".*",
                                    RegexOptions.IgnoreCase))
                                {
                                    context.WriteInfo($"      Softdeleting file {Path.GetFileName(filename)}...");
                                    context.SoftDelete(fullpath);
                                }
                            }

                            foreach (var item in transcription.Items)
                            {
                                var updatedWords = new List<TranscribedWord>();
                                var firstWordStartTime = item.Words.FirstOrDefault()?.StartTime ?? item.StartTime;
                                foreach (var word in item.Words)
                                {
                                    word.FixTiming(
                                        word.StartTime - firstWordStartTime + item.StartTime,
                                        word.EndTime - firstWordStartTime + item.StartTime);
                                }                                
                            }
                        }
                    }
                    context.CurrentWipsub.Save();
                    context.WriteInfo($"Update of WIPSub complete.");
                    context.WriteInfo();
                    break;
            }
        }

        private static IEnumerable<string> GetTranscriptionAnalysis(
            SubtitleGeneratorContext context, 
            Transcription transcription)
        {
            var analysis = transcription.GetAnalysis(context);
            if (analysis != null)
            {
                yield return $"    ForcedTimings Analysis:";
                yield return $"       Number with transcription:    {analysis.NbTimingsWithTranscription}";
                yield return $"       Number without transcription: {analysis.NbTimingsWithoutTranscription}";
                if (analysis.ExtraTranscriptions.Length > 0)
                {
                    yield return $"       Extra transcriptions:  {analysis.ExtraTranscriptions.Length}";
                }

                if (context.IsVerbose)
                {
                    using (var writer = File.CreateText(context.GetPotentialVerboseFilePath($"{transcription.Id}-ANALYSIS-versus-ForcedTimings.txt")))
                    {
                        var extraTranscriptions = analysis.ExtraTranscriptions.ToList();
                        foreach (var item in analysis
                            .TimingsWithOverlapTranscribedTexts
                            .OrderBy(f => f.Key.StartTime))
                        {
                            while (extraTranscriptions.FirstOrDefault()?.StartTime <= item.Key.StartTime)
                            {
                                var extra = extraTranscriptions.First();
                                writer.WriteLine($"[Extra transcription, don't overlap with forced timings] {extra.StartTime} => {extra.EndTime}, {extra.GetFirstTranslatedIfPossible()}");
                                extraTranscriptions.RemoveAt(0);
                            }

                            if (item.Value.Length == 0)
                            {
                                writer.WriteLine($"[No transcription found] {item.Key.StartTime} => {item.Key.EndTime}");
                            }
                            else if (item.Value.Length == 1)
                            {
                                var first = item.Value.First();
                                writer.WriteLine($"{item.Key.StartTime} => {item.Key.EndTime}, {first.TranscribedText.GetFirstTranslatedIfPossible()}");
                            }
                            else
                            {
                                writer.WriteLine($"{item.Key.StartTime} => {item.Key.EndTime}");
                                foreach (var value in item.Value)
                                {
                                    var tt = value.TranscribedText;
                                    writer.WriteLine($"     {tt.StartTime} => {tt.EndTime}, {tt.GetFirstTranslatedIfPossible()}");
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}