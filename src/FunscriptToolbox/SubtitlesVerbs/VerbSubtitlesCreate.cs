using CommandLine;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using log4net;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

            [Option("config", Required = true, HelpText = "")]
            public string ConfigPath { get; set; }
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

        private class VideoSequence
        { 
            public string ContainerFullPath { get; }
            public string[] VideoFullPaths { get; }

            public VideoSequence(string containerFullPath, string[] videoFullPaths)
            {
                ContainerFullPath = containerFullPath;
                VideoFullPaths = videoFullPaths;
            }
        }

        private static IEnumerable<VideoSequence> ToVideoSequences(IEnumerable<string> masterFileList)
        {
            static bool IsVseqPath(string path) => path.EndsWith(".vseq", StringComparison.OrdinalIgnoreCase);

            var sequences = new List<VideoSequence>();

            // Use a HashSet for efficient O(1) lookups to track which files have been
            // claimed as part of a .vseq sequence.
            var claimedVideoPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Make a concrete list to avoid multiple enumerations of the input.
            var allFiles = masterFileList.ToList();

            // --- Pass 1: Process all .vseq files first to claim their video parts. ---
            foreach (var vseqPath in allFiles.Where(f => IsVseqPath(f)))
            {
                var vseqDirectory = PathExtension.SafeGetDirectoryName(vseqPath);

                // Construct the full paths for the video parts.
                var fullVideoPaths = File.ReadAllLines(vseqPath)
                                        .Where(line => !string.IsNullOrWhiteSpace(line))
                                        .Select(relative => Path.GetFullPath(Path.Combine(vseqDirectory, relative.Trim())))
                                        .ToArray();

                // Add this multi-part sequence to our results.
                yield return new VideoSequence(vseqPath, fullVideoPaths);

                // Mark these video files as "claimed" so they are not processed individually later.
                foreach (var videoPath in fullVideoPaths)
                {
                    claimedVideoPaths.Add(videoPath);
                }
            }

            foreach (var fullpath in allFiles
                .Select(path => Path.GetFullPath(path))
                .Where(fullPath => !IsVseqPath(fullPath) && !claimedVideoPaths.Contains(fullPath)))
            {
                yield return new VideoSequence(fullpath, new[] { fullpath });
            }
        }

        public async Task<int> ExecuteAsync()
        {   
            var context = new SubtitleGeneratorContext(
                rs_log,
                r_options.Verbose,
                new FfmpegHelper(),
                r_options.ConfigPath,
                SubtitleGeneratorPrivateConfig.FromFile(
                    Path.ChangeExtension(r_options.ConfigPath, 
                    ".private.config")));

            var errors = new List<string>();
            var userTodoList = new List<string>();
            foreach (var videoSequence in ToVideoSequences(
                r_options
                .Input
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive))
                .Distinct()
                .OrderBy(f => f)))
            {
                var watchGlobal = Stopwatch.StartNew();

                var wipsubFullpath = Path.ChangeExtension(
                    videoSequence.ContainerFullPath,
                    WorkInProgressSubtitles.Extension);

                var wipsub = File.Exists(wipsubFullpath)
                    ? WorkInProgressSubtitles.FromFile(wipsubFullpath)
                    : new WorkInProgressSubtitles(wipsubFullpath, videoSequence.VideoFullPaths);
                wipsub.FinalizeLoad();

                context.ChangeCurrentFile(
                    wipsub, 
                    Path.ChangeExtension(wipsubFullpath, ".wipconfig"));
                UpdateWipSubFileIfNeeded(context);

                try
                {
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