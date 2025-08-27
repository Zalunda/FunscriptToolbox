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

                    // 2. Execute all the workers defined in the config file
                    foreach (var worker in context.Config.Workers)
                    {
                        worker.Execute(context);
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
    }
}