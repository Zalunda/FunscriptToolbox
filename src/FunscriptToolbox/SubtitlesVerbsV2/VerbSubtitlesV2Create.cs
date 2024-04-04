﻿using CommandLine;
using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbsV2.Outputs;
using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using FunscriptToolbox.SubtitlesVerbsV2.Translations;
using log4net;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    [JsonObject(IsReference = false)]
    class VerbSubtitlesV2Create : Verb
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("subtitlesv2.create", aliases: new[] { "subv2.create" }, HelpText = "Create a subtitle from a video (transcribe / translate).")]
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

            [Option("reimporttimings", Required = false, HelpText = "", Default = false)]
            public bool ReimportTimings { get; set; }

            [Option("removetranslations", Required = false, HelpText = "")]
            public string RemoveTranslations { get; set; }
        }

        private readonly Options r_options;

        public VerbSubtitlesV2Create(Options options)
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

            var errors = new List<string>();
            var userTodoList = new List<string>();

            foreach (var inputMp4Fullpath in r_options
                .Input
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive))
                .Distinct()
                .OrderBy(f => f))
            {
                var watchGlobal = Stopwatch.StartNew();
                var ffmpegAudioHelper = new FfmpegAudioHelper();

                var wipsubFullpath = Path.ChangeExtension(
                    inputMp4Fullpath,
                    r_options.Suffix + WorkInProgressSubtitles.Extension);
                var wipsub = File.Exists(wipsubFullpath)
                    ? WorkInProgressSubtitles.FromFile(wipsubFullpath)
                    : new WorkInProgressSubtitles(wipsubFullpath);

                var context = new SubtitleGeneratorContext(
                    rs_log,
                    privateConfig,
                    prefix: $"{Path.GetFileNameWithoutExtension(inputMp4Fullpath)}: ",
                    r_options.Verbose,
                    baseFilePath: Path.Combine(
                        Path.GetDirectoryName(wipsubFullpath) ?? ".",
                        Path.GetFileNameWithoutExtension(wipsubFullpath)),
                    wipsub);

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
                    // 1. Extracting PcmAudio, if not already done.
                    if (wipsub.PcmAudio != null)
                    {
                        context.WriteInfoAlreadyDone($"PcmAudio has already been extracted:");
                        context.WriteInfoAlreadyDone($"    Audio Duration = {wipsub.PcmAudio.Duration}");
                        context.WriteInfoAlreadyDone();
                    }
                    else
                    {
                        context.WriteInfo($"Extracting PCM audio from '{Path.GetFileName(inputMp4Fullpath)}'...");

                        var watchPcmAudio = Stopwatch.StartNew();
                        wipsub.PcmAudio = ffmpegAudioHelper.ExtractPcmAudio(
                            inputMp4Fullpath,
                            extractionParameters: config.FfmpegAudioExtractionParameters);
                        wipsub.Save();

                        context.WriteInfo($"Finished in {watchPcmAudio.Elapsed}:");
                        context.WriteInfo($"    Audio Duration = {wipsub.PcmAudio.Duration}");
                        context.WriteInfo();
                    }

                    // 2. Importing Subtitle Forced Timings, if not already done.
                    var forcedTimingPath = Path.ChangeExtension(
                        inputMp4Fullpath,
                        config.SubtitleForcedTimingsSuffix);
                    if (wipsub.SubtitlesForcedTiming != null && !r_options.ReimportTimings)
                    {
                        context.WriteInfoAlreadyDone($"Subtitle forced timings have already been imported:");
                        context.WriteInfoAlreadyDone($"    Number of timings = {wipsub.SubtitlesForcedTiming.Count}");
                        context.WriteInfoAlreadyDone($"    Timings Duration = {TimeSpan.FromMilliseconds(wipsub.SubtitlesForcedTiming.Sum(f => f.Duration.TotalMilliseconds))}");
                        context.WriteInfoAlreadyDone();
                    }
                    else if (File.Exists(forcedTimingPath))
                    {
                        context.WriteInfo($"Importing forced subtitle timings from '{Path.GetFileName(forcedTimingPath)}'...");

                        wipsub.SubtitlesForcedTiming = new SubtitleForcedTimingCollection(
                            SubtitleFile
                            .FromSrtFile(forcedTimingPath)
                            .Subtitles
                            .Select(
                                subtitle => SubtitleForcedTiming.FromText(
                                    subtitle.StartTime,
                                    subtitle.EndTime,
                                    subtitle.Text)));
                        wipsub.Save();

                        context.WriteInfo($"Finished:");
                        context.WriteInfo($"    Number of timings = {wipsub.SubtitlesForcedTiming.Count}");
                        context.WriteInfo($"    Timings Duration = {TimeSpan.FromMilliseconds(wipsub.SubtitlesForcedTiming.Sum(f => f.Duration.TotalMilliseconds))}");
                        context.WriteInfo();
                    }
                    else if (config.Outputs.Any(f => f.NeedSubtitleForcedTimings))
                    {
                        context.AddUserTodo($"Create the subtitle forced timings file '{Path.GetFileName(forcedTimingPath)}'.");
                    }
                    else
                    {
                        // Ignore subtitle forced timing since it's not used.
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
                            context.WriteInfoAlreadyDone();
                        }
                        else if (!transcriber.IsPrerequisitesMet(context, out var reason))
                        {
                            context.WriteInfo($"Transcription '{transcriber.TranscriptionId}' cannot be done yet: {reason}");
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
                                    ffmpegAudioHelper,
                                    wipsub.PcmAudio,
                                    sourceLanguage);

                                context.WriteInfo($"Finished in {watch.Elapsed}:");
                                context.WriteInfo($"    Number of subtitles = {transcription.Items.Length}");
                                context.WriteInfo();

                                wipsub.Transcriptions.Add(transcription);
                                wipsub.Save();
                            }
                            catch (Exception ex)
                            {
                                context.WriteError($"An error occured while transcribing '{transcriber.TranscriptionId}':\n{ex.Message}");
                            }
                        }

                        // 4. Translating the transcribed text, if not already done.
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
                        // TODO Add logs,
                        output.CreateOutput(
                            context,
                            wipsub);
                    }

                    context.WriteInfo($"Finished in {watchGlobal.Elapsed}.");
                    context.WriteInfo();
                }
                catch (Exception ex)
                {
                    context.WriteError($"Unexpected exception occured: {ex}");
                }

                // TODO Add
            }

            return base.NbErrors;
        }
    }
}