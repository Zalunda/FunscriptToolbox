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

            [Option('v', "autovseq", Required = false, HelpText = "Try to create .vseq files automatically", Default = false)]
            public bool AutoVseq { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }

            [Option("config", Required = true, HelpText = "Path to the main configuration file (e.g., 'FSTB.config').")]
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

        private static IEnumerable<VideoSequence> ToVideoSequences(bool autoVseq, IEnumerable<string> masterFileList)
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

            // --- Pass 2: Process remaining files (Auto-create VSEQ or return individual) ---

            // Get all unclaimed files with full paths and grouped by folder to process folders individually
            foreach (var group in allFiles
                .Select(path => Path.GetFullPath(path))
                .Where(fullPath => !IsVseqPath(fullPath) && !claimedVideoPaths.Contains(fullPath))
                .GroupBy(path => PathExtension.SafeGetDirectoryName(path)))
            {
                var dirPath = group.Key;
                var filesInDir = group.ToList();

                bool groupCreated = false;
                if (autoVseq && filesInDir.Count > 1)
                {
                    // 1. Determine Patterns (Prefix/Suffix) based on File Names (not full paths)
                    var fileNames = filesInDir.Select(Path.GetFileName).ToList();

                    string prefix = GetCommonPrefix(fileNames);
                    string suffix = GetCommonSuffix(fileNames);

                    // 2. Validate Constraints
                    // Check if all files fit the pattern: Prefix + Middle + Suffix
                    // And Middle length <= 2
                    var parsedFiles = new List<(string FullPath, string Middle)>();
                    bool isValidPattern = true;

                    foreach (var filename in fileNames)
                    {
                        int prefixLen = prefix.Length;
                        int suffixLen = suffix.Length;
                        int middleLen = filename.Length - prefixLen - suffixLen;

                        // Constraint: Rest of string (Middle) is less than 2 characters (<= 2)
                        if (middleLen < 0 || middleLen > 2)
                        {
                            isValidPattern = false;
                            break;
                        }

                        string middle = filename.Substring(prefixLen, middleLen);
                        parsedFiles.Add((filename, middle));
                    }

                    if (isValidPattern)
                    {
                        // 3. Sort: First by length of middle part (1 vs 11), then alphanumeric
                        // This ensures "1" comes before "2", and "1" comes before "11"
                        var sortedPaths = parsedFiles
                            .OrderBy(x => x.Middle.Length)
                            .ThenBy(x => x.Middle, StringComparer.OrdinalIgnoreCase)
                            .Select(x => x.FullPath)
                            .ToArray();

                        string vseqName = Path.ChangeExtension($"{prefix}{suffix}", ".vseq");
                        string vseqFullPath = Path.Combine(dirPath, vseqName);
                        File.WriteAllLines(vseqFullPath, sortedPaths);

                        yield return new VideoSequence(vseqFullPath, sortedPaths);
                        groupCreated = true;
                    }
                }

                // Fallback: If grouping disabled, not enough files, or constraints failed
                if (!groupCreated)
                {
                    foreach (var file in filesInDir)
                    {
                        yield return new VideoSequence(file, new[] { file });
                    }
                }
            }
        }

        private static string GetCommonPrefix(List<string> strings)
        {
            string first = strings.First();
            string last = strings.Last();

            int minLen = Math.Min(first.Length, last.Length);
            int i = 0;
            while (i < minLen && first[i] == last[i])
            {
                i++;
            }
            return first.Substring(0, i);
        }

        private static string GetCommonSuffix(List<string> strings)
        {
            string first = strings.First();
            string last = strings.Last();

            int minLen = Math.Min(first.Length, last.Length);
            int i = 1;
            while (i <= minLen && first[first.Length - i] == last[last.Length - i])
            {
                i++;
            }
            return first.Substring(first.Length - i + 1);
        }

        public async Task<int> ExecuteAsync()
        {
            var privateConfig = SubtitleGeneratorPrivateConfig.FromFile(
                    Path.ChangeExtension(r_options.ConfigPath, ".private.config"));

            var context = new SubtitleGeneratorContext(
                rs_log,
                r_options.Verbose,
                new FfmpegHelper(),
                privateConfig);

            var errors = new List<string>();
            var userTodoList = new List<string>();
            foreach (var videoSequence in ToVideoSequences(
                r_options.AutoVseq,
                r_options
                .Input
                .SelectMany(file => HandleStarAndRecusivity(
                    file, 
                    r_options.Recursive, 
                    WorkInProgressSubtitles.BACKUP_FOLDER_SUFFIX))
                .Where(file => !file.EndsWith(".FAST.mp4", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .OrderBy(f => f)))
            {
                try
                {
                    var watchGlobal = Stopwatch.StartNew();

                    var wipsubFullpath = Path.ChangeExtension(
                        videoSequence.ContainerFullPath,
                        WorkInProgressSubtitles.Extension);

                    context.ChangeCurrentFile(null, null);
                    var wipsub = File.Exists(wipsubFullpath)
                        ? WorkInProgressSubtitles.FromFile(wipsubFullpath)
                        : new WorkInProgressSubtitles(wipsubFullpath, videoSequence.VideoFullPaths);
                    wipsub.FinalizeLoad();

                    var config = SubtitleGeneratorConfigLoader.LoadHierarchically(
                                            r_options.ConfigPath,
                                            videoSequence.ContainerFullPath);
                    context.ChangeCurrentFile(config, wipsub);
                    UpdateWipSubFileIfNeeded(context);

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