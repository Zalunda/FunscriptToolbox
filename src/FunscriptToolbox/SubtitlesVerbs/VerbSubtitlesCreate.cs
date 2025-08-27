using CommandLine;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using FunscriptToolbox.SubtitlesVerbs.Infra;
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
            var context = new SubtitleGeneratorContext(
                rs_log,
                r_options.Verbose,
                new FfmpegAudioHelper(),
                SubtitleGeneratorConfig.FromFile(
                    r_options.ConfigPath),
                SubtitleGeneratorPrivateConfig.FromFile(
                    Path.ChangeExtension(r_options.ConfigPath, 
                    ".private.json")),
                Language.FromString(r_options.SourceLanguage ?? "ja"));

            var errors = new List<string>();
            var userTodoList = new List<string>();
            foreach (var inputVideoFullpath in r_options
                .Input
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive))
                .Distinct()
                .OrderBy(f => f))
            {
                var watchGlobal = Stopwatch.StartNew();

                var wipsubFullpath = Path.ChangeExtension(
                    inputVideoFullpath,
                    r_options.Suffix + WorkInProgressSubtitles.Extension);
                var wipsub = File.Exists(wipsubFullpath)
                    ? WorkInProgressSubtitles.FromFile(wipsubFullpath, inputVideoFullpath)
                    : new WorkInProgressSubtitles(wipsubFullpath, inputVideoFullpath);

                context.ChangeCurrentFile(wipsub);
                UpdateWipSubFileIfNeeded(context);

                try
                {
                    // 1. Extracting PcmAudio, if not already done.
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
                        context.WriteInfo($"Extracting PCM audio from '{Path.GetFileName(inputVideoFullpath)}'...");

                        var watchPcmAudio = Stopwatch.StartNew();
                        wipsub.PcmAudio = context.Config.AudioExtractor.ExtractPcmAudio(context, inputVideoFullpath);
                        wipsub.Save();
                        File.WriteAllBytes(pcmFilePath, wipsub.PcmAudio.Data);
                   
                        context.WriteInfo($"Finished in {watchPcmAudio.Elapsed}:");
                        context.WriteInfo($"    Audio Duration = {wipsub.PcmAudio.Duration}");
                        context.WriteInfo();
                    }

                    // 2. Transcribing the audio file, if not already done. Then translating.
                    foreach (var transcriber in context.Config.Transcribers
                        ?.Where(t => t.Enabled)
                        ?? Array.Empty<Transcriber>())
                    {
                        var transcription = wipsub.Transcriptions.FirstOrDefault(
                            t => t.Id == transcriber.TranscriptionId);
                        if (transcription == null)
                        {
                            transcription = new Transcription(
                                transcriber.TranscriptionId,
                                context.OverrideSourceLanguage);
                            wipsub.Transcriptions.Add(transcription);
                        }

                        if (transcription.IsFinished && !transcriber.CanBeUpdated)
                        {
                            context.WriteInfoAlreadyDone($"Transcription '{transcriber.TranscriptionId}' have already been done:");
                            context.WriteInfoAlreadyDone($"    Number of subtitles = {transcription.Items.Count}");
                            context.WriteInfoAlreadyDone($"    Total subtitles duration = {transcription.Items.Sum(f => f.Duration)}");
                            context.WriteInfoAlreadyDone($"    Detected Language = {transcription.Language.LongName}");

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
                                transcriber.Transcribe(context, transcription);

                                if (transcription.IsFinished)
                                {
                                    context.WriteInfo($"Finished in {watch.Elapsed}:");
                                    context.WriteInfo($"    Number of subtitles = {transcription.Items.Count}");
                                    context.WriteInfo($"    Total subtitles duration = {transcription.Items.Sum(f => f.Duration)}");
                                    context.WriteInfo($"    Detected Language = {transcription.Language.LongName}");
                                    foreach (var line in GetTranscriptionAnalysis(context, transcription))
                                    {
                                        context.WriteInfo(line);
                                    }
                                }
                                else
                                {
                                    context.WriteInfo($"Not finished yet in {watch.Elapsed}.");
                                }
                                context.WriteInfo();

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

                        if (transcription.IsFinished)
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
                                        transcription,
                                        translator.TranslationId,
                                        translator.TargetLanguage);
                                    transcription.Translations.Add(translation);
                                }
                                else
                                {
                                    translation.EnsureParent(transcription);
                                }

                                if (translation.IsFinished)
                                {
                                    context.WriteInfoAlreadyDone($"Translation '{transcription.Id}/{translation.Id}' have already been done.");
                                    context.WriteInfoAlreadyDone();
                                }
                                else if (!translator.IsPrerequisitesMet(context, transcription, out var reason))
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

                    // 3. Creating user defined outputs
                    foreach (var output in context.Config.Outputs
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

        private void UpdateWipSubFileIfNeeded(SubtitleGeneratorContext context)
        {
            //switch (context.CurrentWipsub.FormatVersion)
            //{
            //}
        }

        private static IEnumerable<string> GetTranscriptionAnalysis(
            SubtitleGeneratorContext context,
            Transcription transcription)
        {
            yield break;
            //var analysis = transcription.GetAnalysis(context);
            //if (analysis != null)
            //{
            //    yield return $"    ForcedTimings Analysis:";
            //    yield return $"       Number with transcription:    {analysis.NbTimingsWithTranscription}";
            //    yield return $"       Number without transcription: {analysis.TimingsWithoutTranscription.Length}";
            //    if (analysis.ExtraTranscriptions.Length > 0)
            //    {
            //        yield return $"       Extra transcriptions:  {analysis.ExtraTranscriptions.Length}";
            //    }

            //    if (context.IsVerbose)
            //    {
            //        using (var writer = File.CreateText(context.GetPotentialVerboseFilePath($"{transcription.Id}-ANALYSIS-versus-ForcedTimings.txt")))
            //        {
            //            var extraTranscriptions = analysis.ExtraTranscriptions.ToList();
            //            foreach (var item in analysis
            //                .TimingsWithOverlapTranscribedTexts
            //                .OrderBy(f => f.Key.StartTime))
            //            {
            //                while (extraTranscriptions.FirstOrDefault()?.StartTime <= item.Key.StartTime)
            //                {
            //                    var extra = extraTranscriptions.First();
            //                    writer.WriteLine($"[Extra transcription, don't overlap with forced timings] {extra.StartTime} => {extra.EndTime}, {extra.GetFirstTranslatedIfPossible()}");
            //                    extraTranscriptions.RemoveAt(0);
            //                }

            //                if (item.Value.Length == 0)
            //                {
            //                    writer.WriteLine($"[No transcription found] {item.Key.StartTime} => {item.Key.EndTime}");
            //                }
            //                else if (item.Value.Length == 1)
            //                {
            //                    var first = item.Value.First();
            //                    writer.WriteLine($"{item.Key.StartTime} => {item.Key.EndTime}, {first.TranscribedText.GetFirstTranslatedIfPossible()}");
            //                }
            //                else
            //                {
            //                    writer.WriteLine($"{item.Key.StartTime} => {item.Key.EndTime}");
            //                    foreach (var value in item.Value)
            //                    {
            //                        var tt = value.TranscribedText;
            //                        writer.WriteLine($"     {tt.StartTime} => {tt.EndTime}, {tt.GetFirstTranslatedIfPossible()}");
            //                    }
            //                }
            //            }
            //        }
            //    }
            //}
        }
    }
}