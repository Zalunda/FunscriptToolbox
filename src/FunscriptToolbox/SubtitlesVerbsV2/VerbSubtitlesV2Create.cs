using CommandLine;
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

                    var context = new SubtitleGeneratorContext(
                        rs_log,
                        r_options.Verbose,
                        baseFilePath,
                        wipsub);

                    // TODO Add info/verbose logs

                    // TODO WriteMessage if already done
                    wipsub.PcmAudio ??= ffmpegAudioHelper.ExtractPcmAudio(
                        context,
                        inputMp4Fullpath, 
                        extractionParameters: config.FfmpegAudioExtractionParameters);
                    wipsub.Save();

                    // TODO WriteMessage if already done
                    wipsub.SubtitlesForcedTiming ??= ImportForcedLocations(
                            context,
                            inputMp4Fullpath,
                            config.SubtitleForcedLocationSuffix);
                    wipsub.Save();

                    var sourceLanguage = Language.FromString(r_options.SourceLanguage);

                    foreach (var transcriber in config.Transcribers
                        ?.Where(t => t.Enabled) 
                        ?? Array.Empty<Transcriber>())
                    {
                        var transcription = wipsub.Transcriptions.FirstOrDefault(
                            t => t.Id == transcriber.TranscriptionId);
                        if (transcription == null)
                        {
                            transcription = transcriber.Transcribe(
                                context,
                                ffmpegAudioHelper,
                                wipsub.PcmAudio,
                                sourceLanguage);
                            if (transcription != null)
                            {
                                wipsub.Transcriptions.Add(transcription);
                                wipsub.Save();
                            }
                        }
                        else
                        {
                            // TODO
                        }

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

                                translator.Translate(
                                    context,
                                    baseFilePath,
                                    transcription,
                                    translation);
                            }
                        }
                    }

                    foreach (var output in config.Outputs
                        ?.Where(o => o.Enabled) 
                        ?? Array.Empty<SubtitleOutput>())
                    {
                        output.CreateOutput(
                            context,
                            wipsub);
                    }

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

        private SubtitleForcedTimingCollection ImportForcedLocations(
            SubtitleGeneratorContext context,
            string inputMp4Fullpath,
            string forcedLocationSuffixe)
        {
            var path = Path.ChangeExtension(
                inputMp4Fullpath, 
                forcedLocationSuffixe);
            if (File.Exists(path))
            {
                context.WriteInfo($"Importing forced subtitle timing file '{Path.GetFileName(forcedLocationSuffixe)}'...");
                var humanVadSrtFile = SubtitleFile.FromSrtFile(
                    path);

                var result = new SubtitleForcedTimingCollection(
                    humanVadSrtFile.Subtitles.Select(
                        subtitle => SubtitleForcedTiming.FromText(
                            subtitle.StartTime,
                            subtitle.EndTime,
                            subtitle.Text)));
                context.WriteInfo($"Finished:");
                context.WriteInfo($"    Number of items = {result.Count}");
                context.WriteInfo($"    AudioDuration = {TimeSpan.FromMilliseconds(result.Sum(f => f.Duration.TotalMilliseconds))}");
                return result;
            }
            else
            {
                context.WriteInfo($"Forced subtitle timing file '{Path.GetFileName(forcedLocationSuffixe)}' doesn't exists yet.");
                return null;
            }
        }
    }
}