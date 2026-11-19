using CommandLine;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.Progress;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using log4net;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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

            [Option("ui", Required = false, HelpText = "Process all the videos in parallel and show the progress on a web page served on this port (e.g., --ui 5055). A browser is opened on it.")]
            public int? UiPort { get; set; }
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

            var locks = new SharedLocks();
            var webOutput = r_options.UiPort != null ? new WebUiOutput(locks) : null;
            IUserOutput output = (IUserOutput)webOutput ?? new ConsoleUserOutput();
            WebUiServer webServer = null;
            if (webOutput != null)
            {
                webServer = new WebUiServer(webOutput, r_options.UiPort.Value);
                webServer.Start();
                Console.WriteLine($"Progress UI available on {webServer.Url}");
                webServer.OpenBrowser();
            }

            try
            {
                var ffmpegHelper = new FfmpegHelper(locks);
                SubtitleGeneratorContext CreateContext(IUserOutput contextOutput) => new SubtitleGeneratorContext(
                    rs_log,
                    r_options.Verbose,
                    ffmpegHelper,
                    privateConfig,
                    contextOutput,
                    locks);

                // The sequences are listed once, before anything is started (creating the .vseq files, if needed).
                var videoSequences = ToVideoSequences(
                    r_options.AutoVseq,
                    r_options
                    .Input
                    .SelectMany(file => HandleStarAndRecusivity(
                        file,
                        r_options.Recursive,
                        WorkInProgressSubtitles.BACKUP_FOLDER_SUFFIX))
                    .Where(file => !file.EndsWith(".FAST.mp4", StringComparison.OrdinalIgnoreCase))
                    .Distinct()
                    .OrderBy(f => f))
                    .OrderBy(vs => vs.ContainerFullPath)
                    // The merged configuration is read first: if all its workers are disabled, there is nothing
                    // to do for the item (the .wipsubtitles is not loaded and the item is not added to the UI).
                    .Select(vs => (Sequence: vs, Config: TryLoadConfig(vs)))
                    .Where(item => item.Config == null || item.Config.Workers?.Any(w => w.Enabled) == true)
                    .ToList();

                var context = CreateContext(output);
                if (webOutput == null)
                {
                    // Console mode: one video at a time, using a single context.
                    foreach (var (videoSequence, config) in videoSequences)
                    {
                        // (in the console, the tasks of the sequence are combined in the progress line)
                        using var sequenceNode = output.CreateRootNode("Sequence", GetDisplayName(videoSequence));
                        sequenceNode.Start();
                        ProcessSequence(context, videoSequence, config, showHeader: true);
                    }
                }
                else
                {
                    // Web UI mode: all the videos at the same time, each one on its own thread with its own context.
                    // (dedicated threads because the AI/HTTP code blocks while waiting: no thread pool starvation)
                    var sequenceContexts = new List<SubtitleGeneratorContext>();
                    var threads = new List<Thread>();
                    foreach (var (videoSequence, config) in videoSequences)
                    {
                        var sequenceContext = CreateContext(output);
                        var sequenceNode = output.CreateRootNode("Sequence", GetDisplayName(videoSequence));
                        sequenceContexts.Add(sequenceContext);

                        var sequence = videoSequence;
                        var sequenceConfig = config;
                        var sequenceOrdinal = sequenceContexts.Count;
                        var thread = new Thread(() =>
                        {
                            // The first sequences have priority on the shared resources (ffmpeg, GPU, ...): they finish first,
                            // instead of having all the sequences progressing slowly, one screenshot/clip at a time each.
                            LockPriority.SetForSequence(sequenceOrdinal);
                            sequenceNode.Start();
                            try
                            {
                                ProcessSequence(sequenceContext, sequence, sequenceConfig, showHeader: false);
                                if (sequenceContext.Errors.Count > 0)
                                {
                                    sequenceNode.SetState(TaskState.Failed, $"{sequenceContext.Errors.Count} error(s)");
                                }
                            }
                            finally
                            {
                                sequenceNode.Dispose();
                            }
                        })
                        { Name = $"Sequence {sequenceContexts.Count}" };
                        threads.Add(thread);
                    }
                    threads.ForEach(t => t.Start());
                    threads.ForEach(t => t.Join());

                    foreach (var sequenceContext in sequenceContexts)
                    {
                        context.Errors.AddRange(sequenceContext.Errors);
                        context.UserTodoList.AddRange(sequenceContext.UserTodoList);
                    }
                }

                context.ForgetCurrentFile();

                // Write final report
                WriteFinalReport(context);
                if (webOutput != null)
                {
                    // Also on the console (the web page already has it, in the 'General' task).
                    var consoleContext = CreateContext(new ConsoleUserOutput());
                    consoleContext.Errors.AddRange(context.Errors);
                    consoleContext.UserTodoList.AddRange(context.UserTodoList);
                    WriteFinalReport(consoleContext);

                    webOutput.MarkFinished();
                    Console.WriteLine($"All done. The progress UI is still available on {webServer.Url}");
                    Console.WriteLine("Click on 'Close FunscriptToolbox' in the page, or press ENTER here, to exit.");
                    webServer.WaitForExitRequest();
                }
                return base.NbErrors;
            }
            finally
            {
                webServer?.Dispose();
            }
        }

        private static string GetDisplayName(VideoSequence videoSequence)
        {
            var wipsubFullpath = Path.ChangeExtension(videoSequence.ContainerFullPath, WorkInProgressSubtitles.Extension);
            return PathExtension.GetRelativePath(Environment.CurrentDirectory, Path.ChangeExtension(Path.GetFullPath(wipsubFullpath), null))?.ToString();
        }

        /// <summary>
        /// Reads the merged configuration of an item. Returns null if it cannot be loaded
        /// (the item is then processed normally, so that the loading error is reported as usual).
        /// </summary>
        private SubtitleGeneratorConfig TryLoadConfig(VideoSequence videoSequence)
        {
            try
            {
                return SubtitleGeneratorConfigLoader.LoadHierarchically(
                    r_options.ConfigPath,
                    videoSequence.ContainerFullPath);
            }
            catch (Exception ex)
            {
                rs_log.Error($"Error while loading the configuration for '{videoSequence.ContainerFullPath}'.", ex);
                return null;
            }
        }

        private void ProcessSequence(SubtitleGeneratorContext context, VideoSequence videoSequence, SubtitleGeneratorConfig preloadedConfig, bool showHeader)
        {
            try
            {
                var watchGlobal = Stopwatch.StartNew();

                var wipsubFullpath = Path.ChangeExtension(
                    videoSequence.ContainerFullPath,
                    WorkInProgressSubtitles.Extension);

                context.ChangeCurrentFile(null, null);
                if (showHeader)
                {
                    context.Output.WriteInfo($"------ {GetDisplayName(videoSequence)} ------", null, false);
                }

                context.RunStep("Load the .wipsubtitles and the configuration", _ =>
                {
                    var wipsub = File.Exists(wipsubFullpath)
                        ? WorkInProgressSubtitles.FromFile(wipsubFullpath)
                        : new WorkInProgressSubtitles(wipsubFullpath, videoSequence.VideoFullPaths);
                    wipsub.FinalizeLoad();

                    var config = preloadedConfig ?? SubtitleGeneratorConfigLoader.LoadHierarchically(
                                            r_options.ConfigPath,
                                            videoSequence.ContainerFullPath);
                    wipsub.SetBackupParentFolder(config.Global?.BackupParentFolder);
                    context.ChangeCurrentFile(config, wipsub);
                    UpdateWipSubFileIfNeeded(context);
                });

                if (context.Config.Workers != null)
                {
                    // Shows all the steps in the task tree of the web UI right away (as 'pending')
                    foreach (var worker in context.Config.Workers.Where(w => w.Enabled))
                    {
                        context.PlanWorkerTask(worker, worker.DisplayName);
                    }

                    foreach (var worker in context.Config.Workers)
                    {
                        worker.Execute(context);
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

        private static void WriteFinalReport(SubtitleGeneratorContext context)
        {
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
        }

        private void UpdateWipSubFileIfNeeded(SubtitleGeneratorContext context)
        {
            //switch (context.CurrentWipsub.FormatVersion)
            //{
            //}
        }
    }
}