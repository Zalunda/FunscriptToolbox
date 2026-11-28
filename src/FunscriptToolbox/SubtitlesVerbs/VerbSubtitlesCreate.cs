using CommandLine;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Progress;
using FunscriptToolbox.SubtitlesVerbs.Inputs;
using log4net;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

            [Option("ui", Required = false, HelpText = "The port of the web page that shows the progress (e.g., --ui 5056): all the videos are processed in parallel. Default: 5055 (or the port given with --runasserver).")]
            public int? UiPort { get; set; }

            [Option("openbrowser", Required = false, HelpText = "Open the progress page in the default browser at the start.", Default = false)]
            public bool OpenBrowser { get; set; }

            [Option("background", Required = false, HelpText = "Runs without a console. The application starts itself again, hidden, and this one ends right away: the page is the only way to follow it. If it's already running on this port, it only opens the browser (with --openbrowser) and ends.", Default = false)]
            public bool Background { get; set; }

            [Option("runasserver", Required = false, HelpText = "Runs as a server (the web UI is implied): '<host>[:<port>]', ex. 'localhost:5055' (only this machine) or '*:5055' (the page can be opened from other machines: on Windows, needs an URL reservation and a firewall rule, asked once to Windows with an administrator prompt; if refused, the message gives the commands). The port can also be given with --ui (default: 5055). The page has no 'Close' button (only 'Pause all'): the application keeps running, 'Rescan' starts the new scenes; stop it by ending its process. Warning: there is no password, use it only on a trusted network.")]
            public string RunAsServer { get; set; }

            /// <summary>--runasserver is used.</summary>
            public bool IsServer => RunAsServer != null;

            /// <summary>(internal) The copy started by --background.</summary>
            [Option("backgroundchild", Required = false, Hidden = true, Default = false)]
            public bool IsBackgroundChild { get; set; }
        }

        private readonly Options r_options;

        public VerbSubtitlesCreate(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        /// <summary>The port of the web page when it's not given with --ui or --runasserver.</summary>
        private const int DefaultUiPort = 5055;

        // The web UI: port and host (from --ui and --runasserver)
        private int? m_uiPort;
        private string m_uiHost;

        public int Execute()
        {
            m_uiPort = r_options.UiPort;
            m_uiHost = null;
            if (r_options.IsServer)
            {
                // '<host>[:<port>]' (the port can also come from --ui)
                var value = r_options.RunAsServer.Trim();
                var colon = value.LastIndexOf(':');
                m_uiHost = colon >= 0 ? value.Substring(0, colon) : value;
                if (colon >= 0)
                {
                    if (!int.TryParse(value.Substring(colon + 1), out var port))
                    {
                        WriteError($"Invalid port in --runasserver '{r_options.RunAsServer}' (expected '<host>[:<port>]', ex. '*:5055').");
                        return 1;
                    }
                    if (m_uiPort != null && m_uiPort != port)
                    {
                        WriteError($"--runasserver '{r_options.RunAsServer}' and --ui {m_uiPort} give two different ports: give it only once.");
                        return 1;
                    }
                    m_uiPort = port;
                }
            }

            // (no port given with --ui or --runasserver: the default port)
            m_uiPort ??= DefaultUiPort;
            if (r_options.Background && !r_options.IsBackgroundChild)
            {
                return StartInBackground();
            }
            return Task.Run<int>(() => ExecuteAsync()).Result;
        }

        /// <summary>
        /// --background: starts the application again, without a console window (with the same arguments), and ends right away.
        /// If it's already running on this port: only opens the browser on it (with --openbrowser).
        /// </summary>
        private int StartInBackground()
        {
            var localUrl = $"http://localhost:{m_uiPort.Value}/";
            if (WebUiServer.IsAnswering(localUrl))
            {
                Console.WriteLine($"FunscriptToolbox is already running on {localUrl}: nothing else is started.");
                if (r_options.OpenBrowser)
                {
                    WebUiServer.OpenBrowser(localUrl);
                }
                return 0;
            }

            // The same arguments, without --background (the copy knows it's the one in the background)
            var arguments = (Program.EffectiveArguments ?? Environment.GetCommandLineArgs().Skip(1).ToArray())
                .Where(arg => !string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase))
                .Concat(new[] { "--backgroundchild" })
                .ToList();

            // (started with 'dotnet FunscriptToolbox.dll': the copy too)
            var processPath = Environment.ProcessPath;
            var startInfo = new ProcessStartInfo(processPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            }
            foreach (var arg in arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

            var process = Process.Start(startInfo)
                ?? throw new Exception("Can't start FunscriptToolbox in the background.");
            var message = r_options.IsServer
                ? $"FunscriptToolbox started in the background, as a server (process {process.Id}). Follow it on {localUrl} (its log is in the log file). It can't be closed from the page: end its process to stop it."
                : $"FunscriptToolbox started in the background (process {process.Id}). Follow it, and close it, on {localUrl} (its log is in the log file).";
            rs_log.Info(message);
            Console.WriteLine(message);
            return 0;
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

            // What is shared by all the scenes (ex. how many ffmpeg at the same time): read once, from the main configuration only.
            var globalShared = SubtitleGeneratorConfigLoader.LoadGlobalShared(r_options.ConfigPath);
            var locks = new SharedLocks(globalShared?.MaxConcurrent);
            // (as a server: no 'Close' in the page, only 'Pause all')
            var webOutput = new WebUiOutput(locks) { IsServer = r_options.IsServer };
            IUserOutput output = webOutput;
            var webServer = new WebUiServer(webOutput, m_uiPort.Value, m_uiHost);
            webServer.Start();
            Console.WriteLine($"Progress UI available on {webServer.Url}"
                + (webServer.IsReachableFromNetwork ? $" (from another machine: http://{(webServer.Host == "*" || webServer.Host == "+" ? Environment.MachineName : webServer.Host)}:{webServer.Port}/)" : string.Empty));
            rs_log.Info($"Progress UI listening on {webServer.ListenUrl}");
            if (r_options.OpenBrowser)
            {
                webServer.OpenBrowser();
            }
            else if (!r_options.IsBackgroundChild)
            {
                Console.WriteLine("Open it in a browser (or add the --openbrowser parameter to open it automatically each time).");
            }
            // (warns in the console if nobody opens the page; in the background, there is no console to warn)
            if (!r_options.IsBackgroundChild)
            {
                webServer.StartClientWatcher();
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

                // The sequences are listed before anything is started (creating the .vseq files, if needed).
                var videoSequences = ListVideoSequences();

                // How each scene is named in the UI (short name, metadata from its path). A configuration problem is reported once.
                var reportedConfigErrors = new HashSet<string>();
                RootNodeInfo GetRootNodeInfo(VideoSequence videoSequence, SubtitleGeneratorConfig config)
                {
                    var info = BuildRootNodeInfo(videoSequence, config);
                    // (a configuration that can't be loaded: the scene is started, so that the loading error is reported as usual)
                    info.IsInactive = config != null && config.Workers?.Any(w => w.Enabled) != true;
                    // (its priority on the shared resources, from its metadata: the rules of 'GlobalShared', the same for all the scenes)
                    ApplyScenePriority(info, globalShared?.ScenePriorities);
                    if (info.ConfigError != null && reportedConfigErrors.Add(info.ConfigError))
                    {
                        rs_log.Error(info.ConfigError);
                        output.WriteError(info.ConfigError);
                    }
                    return info;
                }

                // Web UI mode: all the videos at the same time, each one on its own thread with its own context.
                // (dedicated threads because the AI/HTTP code blocks while waiting: no thread pool starvation)
                var scenes = new List<WebUiScene>();
                var lastOrdinal = 0;
                void StartScene(VideoSequence videoSequence, SubtitleGeneratorConfig config, WebUiScene sceneToRestart)
                {
                    var info = GetRootNodeInfo(videoSequence, config);
                    // (not the count of the list: a scene can be removed from it, see 'Rescan')
                    var scene = sceneToRestart ?? new WebUiScene { Ordinal = ++lastOrdinal };
                    scene.Sequence = videoSequence;
                    scene.Context = CreateContext(output);
                    scene.ConfigHash = config?.MergedConfigHash;
                    // (a scene started again keeps its place in the list: its old tasks are replaced)
                    scene.Node = sceneToRestart == null
                        ? output.CreateRootNode("Sequence", GetDisplayName(videoSequence), info)
                        : webOutput.ReplaceRootNode(sceneToRestart.Node, "Sequence", GetDisplayName(videoSequence), info);
                    if (sceneToRestart == null)
                        scenes.Add(scene);

                    var sequenceContext = scene.Context;
                    var sequenceNode = scene.Node;

                    // No enabled worker in its configuration: listed, but nothing is started (Retry / Rescan: when its configuration has some)
                    if (info.IsInactive)
                    {
                        scene.Thread = null;
                        sequenceNode.SetState(TaskState.Skipped, "no enabled worker in its configuration");
                        sequenceNode.Dispose();
                        return;
                    }

                    scene.Thread = new Thread(() =>
                    {
                        // The first sequences have priority on the shared resources (ffmpeg, GPU, ...): they finish first,
                        // instead of having all the sequences progressing slowly, one screenshot/clip at a time each.
                        // (and before that: the priority of the scene, from its metadata, see GlobalShared.ScenePriorities)
                        LockPriority.SetForSequence(info.PriorityLevel, scene.Ordinal);
                        sequenceNode.Start();
                        try
                        {
                            ProcessSequence(sequenceContext, videoSequence, config);
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
                    {
                        Name = $"Sequence {scene.Ordinal}",
                        // (background: a 'Force close' can end the process while scenes are running; otherwise, the main thread waits for them)
                        IsBackground = true
                    };
                    scene.Thread.Start();
                }

                foreach (var (videoSequence, config) in videoSequences)
                {
                    StartScene(videoSequence, config, null);
                }

                // 'Rescan' (button of the page): the new scenes on disk are started, and the blocked / inactive scenes are checked again
                // (ex. their manual-input file is now there, workers are enabled in their configuration). The other scenes are not touched
                // (running, finished, failed, stopped).
                // A scene that is not on disk anymore at its place (moved to another folder, ex. from '01-Staging' to 'ManualHQWorkflow-G4',
                // or deleted) is removed from the list, and at its new place, it's a new scene (loaded with the configuration of that folder).
                // Except if it's running: it stays, and its new place is not started (two threads on the same scene); 'Rescan' again when it's done.
                // A scene on hold ('Pause scene') is removed too: its thread stays paused forever, nothing more is done at its old place.
                void Rescan()
                {
                    var nbNew = 0;
                    var nbRetried = 0;
                    var nbActivated = 0;
                    var nbRemoved = 0;
                    var stillRunning = new List<WebUiScene>();
                    foreach (var scene in scenes.ToArray())
                    {
                        if (File.Exists(scene.Sequence.ContainerFullPath))
                            continue;
                        if (scene.Thread?.IsAlive == true && webOutput.GetNodeId(scene.Node) is int nodeId && !webOutput.IsScenePaused(nodeId))
                        {
                            output.WriteInfo($"Rescan: '{GetDisplayName(scene.Sequence)}' is not on disk anymore, but it's still running: kept in the list.", null, false);
                            stillRunning.Add(scene);
                            continue;
                        }
                        output.WriteInfo($"Rescan: '{GetDisplayName(scene.Sequence)}' is not on disk anymore (moved or deleted): removed from the list.", null, false);
                        webOutput.RemoveRootNode(scene.Node);
                        scenes.Remove(scene);
                        nbRemoved++;
                    }

                    var nbNotStarted = 0;
                    foreach (var (videoSequence, config) in ListVideoSequences())
                    {
                        var existing = scenes.FirstOrDefault(s => string.Equals(s.Sequence.ContainerFullPath, videoSequence.ContainerFullPath, StringComparison.OrdinalIgnoreCase));
                        if (existing == null)
                        {
                            var running = stillRunning.FirstOrDefault(s => string.Equals(Path.GetFileName(s.Sequence.ContainerFullPath), Path.GetFileName(videoSequence.ContainerFullPath), StringComparison.OrdinalIgnoreCase));
                            if (running != null)
                            {
                                output.WriteInfo($"Rescan: '{GetDisplayName(videoSequence)}' not started: the same scene is still running at its old place ('{GetDisplayName(running.Sequence)}'). 'Rescan' again when it's done.", null, false);
                                nbNotStarted++;
                                continue;
                            }
                            StartScene(videoSequence, config, null);
                            nbNew++;
                        }
                        else if (existing.Thread == null)
                        {
                            // (inactive: started when its configuration has enabled workers now)
                            if (config == null || config.Workers?.Any(w => w.Enabled) == true)
                            {
                                StartScene(videoSequence, config, existing);
                                nbActivated++;
                            }
                        }
                        else if (!existing.Thread.IsAlive && existing.Context.BlockedWorkers.Count > 0 && existing.Context.Errors.Count == 0)
                        {
                            StartScene(videoSequence, config, existing);
                            nbRetried++;
                        }
                    }
                    var result = $"Rescan: {nbNew} new scene(s) found, {nbRetried} blocked scene(s) checked again"
                        + (nbRemoved > 0 ? $", {nbRemoved} scene(s) not on disk anymore removed" : "")
                        + (nbNotStarted > 0 ? $", {nbNotStarted} moved scene(s) not started (still running at their old place)" : "")
                        + (nbActivated > 0 ? $", {nbActivated} inactive scene(s) started (workers enabled in their configuration)." : ".");
                    output.WriteInfo(result, null, false);
                    webOutput.SetRescanState(isRescanning: false, result);
                }

                // Where a scene is now on disk, if it was moved to another folder (null: not found, or more than one with its file name).
                (VideoSequence Sequence, SubtitleGeneratorConfig Config)? FindNewPlace(WebUiScene scene)
                {
                    var fileName = Path.GetFileName(scene.Sequence.ContainerFullPath);
                    var found = ListVideoSequences()
                        .Where(vs => string.Equals(Path.GetFileName(vs.Sequence.ContainerFullPath), fileName, StringComparison.OrdinalIgnoreCase)
                            && !scenes.Any(s => string.Equals(s.Sequence.ContainerFullPath, vs.Sequence.ContainerFullPath, StringComparison.OrdinalIgnoreCase)))
                        .ToArray();
                    return found.Length == 1 ? found[0] : ((VideoSequence, SubtitleGeneratorConfig)?)null;
                }

                // The configuration files (the main one, the '.override.config' / '.override.user.config' of the folders under it), changed on
                // disk (by the web UI or by hand): the scenes under the folder of the file are checked again, a moment after the last change
                // (an editor can save a file in several steps). A scene whose configuration would not be the same anymore is flagged (see
                // WebUiOutput.SetSceneConfigChange): once done, it's in the section 'config changed' of the page (Retry / 'Reload all').
                // (what is read only once, at the start of the application: 'GlobalShared' (ex. MaxConcurrent) and the '.private.config')
                var configRootFolder = Path.GetDirectoryName(Path.GetFullPath(r_options.ConfigPath));
                var configFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    Path.GetFileName(r_options.ConfigPath),
                    Path.GetFileNameWithoutExtension(r_options.ConfigPath) + ".override.config",
                    Path.GetFileNameWithoutExtension(r_options.ConfigPath) + ".override.user.config"
                };
                var changedConfigFolders = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
                long lastConfigChangeTicks = 0;
                void OnConfigFileEvent(string fullPath)
                {
                    if (fullPath == null || !configFileNames.Contains(Path.GetFileName(fullPath)))
                        return;
                    changedConfigFolders[Path.GetDirectoryName(fullPath)] = 0;
                    Interlocked.Exchange(ref lastConfigChangeTicks, Environment.TickCount64);
                    webOutput.RequestWakeUp();
                }
                using var configWatcher = new FileSystemWatcher(configRootFolder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                configWatcher.Filters.Add(Path.GetFileNameWithoutExtension(r_options.ConfigPath) + "*.config");
                configWatcher.Changed += (_, e) => OnConfigFileEvent(e.FullPath);
                configWatcher.Created += (_, e) => OnConfigFileEvent(e.FullPath);
                configWatcher.Deleted += (_, e) => OnConfigFileEvent(e.FullPath);
                configWatcher.Renamed += (_, e) => { OnConfigFileEvent(e.OldFullPath); OnConfigFileEvent(e.FullPath); };
                // (too many changes at once, the events were lost: everything is checked again)
                configWatcher.Error += (_, e) =>
                {
                    changedConfigFolders[configRootFolder] = 0;
                    Interlocked.Exchange(ref lastConfigChangeTicks, Environment.TickCount64);
                    webOutput.RequestWakeUp();
                };
                configWatcher.EnableRaisingEvents = true;

                void CheckConfigChanges(string[] folders)
                {
                    // (the scenes of a folder: the same configuration files, read once)
                    var byFolder = new Dictionary<string, (string Hash, string Error)>(StringComparer.OrdinalIgnoreCase);
                    foreach (var scene in scenes)
                    {
                        var container = Path.GetFullPath(scene.Sequence.ContainerFullPath);
                        if (!folders.Any(f => container.StartsWith(f.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        var folder = Path.GetDirectoryName(container);
                        if (!byFolder.TryGetValue(folder, out var loaded))
                        {
                            try
                            {
                                loaded = (SubtitleGeneratorConfigLoader.LoadHierarchically(r_options.ConfigPath, scene.Sequence.ContainerFullPath).MergedConfigHash, null);
                            }
                            catch (Exception ex)
                            {
                                loaded = (null, ex.Message);
                            }
                            byFolder[folder] = loaded;
                        }
                        // (back as it was, ex. a change undone: not flagged anymore)
                        webOutput.SetSceneConfigChange(scene.Node,
                            loaded.Error != null ? $"Its configuration can't be read: {loaded.Error}"
                            : loaded.Hash != scene.ConfigHash ? "changed"
                            : null);
                    }
                }

                // 'Redo this step', on a scene whose thread has ended (see the main loop)
                var pendingRedos = new List<(int SceneId, string WorkerKey)>();
                void RedoStep(WebUiScene scene, int sceneId, string workerKey)
                {
                    try
                    {
                        var config = scene.Context.Config;
                        var wip = scene.Context.WIP;
                        var workers = config == null ? null : Infra.SceneFlowGraph.GetWorkers(config);
                        var worker = workers != null && workerKey.StartsWith("w") && int.TryParse(workerKey.Substring(1), out var workerIndex) && workerIndex >= 0 && workerIndex < workers.Length
                            ? workers[workerIndex]
                            : null;
                        if (worker == null || wip == null)
                        {
                            output.WriteError($"Redo: step '{workerKey}' of '{GetDisplayName(scene.Sequence)}' not found (its configuration or its .wipsubtitles is not loaded).");
                            return;
                        }
                        var dataId = (worker as Infra.TrackProducer)?.Id;
                        var container = wip.Tracks.FirstOrDefault(t => t.Id == dataId);
                        if (container == null)
                        {
                            output.WriteError($"Redo: '{worker.DisplayName}' of '{GetDisplayName(scene.Sequence)}' has nothing to do again (no transcription / translation '{dataId}').");
                            return;
                        }
                        var allIds = new HashSet<string>(wip.Tracks.Select(t => t.Id));
                        var newId = $"{dataId}-OLD";
                        for (var n = 2; allIds.Contains(newId); n++)
                            newId = $"{dataId}-OLD{n}";
                        container.ChangeId(newId);
                        // (asked to be done again: not on hold anymore)
                        wip.IsOnHold = false;
                        wip.Save();
                        webOutput.SetScenePaused(sceneId, false);
                        output.WriteInfo($"Redo: '{worker.DisplayName}' of '{GetDisplayName(scene.Sequence)}': what it produced is kept as '{newId}' in the .wipsubtitles; the scene is started again.", null, false);
                        webOutput.RequestRetry(sceneId);
                    }
                    catch (Exception ex)
                    {
                        output.WriteError($"Redo: can't do '{workerKey}' of '{GetDisplayName(scene.Sequence)}' again: {ex.Message}");
                    }
                }

                var isReported = false;
                while (true)
                {
                    // (the configuration files changed: checked once nothing changed for a second)
                    if (!changedConfigFolders.IsEmpty && Environment.TickCount64 - Interlocked.Read(ref lastConfigChangeTicks) >= 1000)
                    {
                        var folders = changedConfigFolders.Keys.ToArray();
                        foreach (var folder in folders)
                            changedConfigFolders.TryRemove(folder, out _);
                        try
                        {
                            CheckConfigChanges(folders);
                        }
                        catch (Exception ex)
                        {
                            rs_log.Error("Can't check the scenes after a change of the configuration files.", ex);
                        }
                    }

                    // (closing: nothing new is started, even what was asked just before)
                    if (webOutput.ConsumeRescanRequest() && !webOutput.IsClosing)
                    {
                        try
                        {
                            Rescan();
                        }
                        catch (Exception ex)
                        {
                            output.WriteError($"Error during the rescan: {ex.Message}");
                            webOutput.SetRescanState(isRescanning: false, $"Rescan failed: {ex.Message} (see the General log)");
                        }
                    }
                    else if (webOutput.IsRescanning && webOutput.IsClosing)
                    {
                        // (asked just before 'Close': not done)
                        webOutput.SetRescanState(isRescanning: false, "Rescan not done: the application is closing.");
                    }

                    // 'Redo this step' (the Flow inspector, on a step): what the step produced (its transcription / translation) is kept in the
                    // .wipsubtitles under another id ('<id>-OLD', '<id>-OLD2'...: to compare), then the scene is started again (like Retry):
                    // the step is done again. A scene that runs or is on hold first ends at its next safe point (SceneRestartException, see
                    // SubtitleGeneratorContext.PauseIfClosing: what was received is saved), then it's done; it's not on hold anymore after.
                    for (var redo = webOutput.ConsumeRedoStepRequest(); redo != null; redo = webOutput.ConsumeRedoStepRequest())
                    {
                        var (redoSceneId, workerKey) = redo.Value;
                        var scene = scenes.FirstOrDefault(s => webOutput.GetNodeId(s.Node) == redoSceneId);
                        if (scene == null || webOutput.IsClosing)
                            continue;
                        if (scene.Thread?.IsAlive == true)
                        {
                            output.WriteInfo($"Redo: '{GetDisplayName(scene.Sequence)}' ends at its next safe point (what was received is saved), then the step is done again.", null, false);
                            webOutput.SetSceneRestartRequested(redoSceneId, true);
                            pendingRedos.RemoveAll(r => r.SceneId == redoSceneId);
                            pendingRedos.Add((redoSceneId, workerKey));
                        }
                        else
                        {
                            RedoStep(scene, redoSceneId, workerKey);
                        }
                    }
                    // (the scenes asked to end for a redo: done once their thread has ended)
                    foreach (var pending in pendingRedos.ToArray())
                    {
                        var scene = scenes.FirstOrDefault(s => webOutput.GetNodeId(s.Node) == pending.SceneId);
                        if (scene != null && scene.Thread?.IsAlive == true && !webOutput.IsClosing)
                            continue;
                        pendingRedos.Remove(pending);
                        webOutput.SetSceneRestartRequested(pending.SceneId, false);
                        if (scene != null && !webOutput.IsClosing)
                            RedoStep(scene, pending.SceneId, pending.WorkerKey);
                    }

                    // 'Retry' (button of a scene in the page): the scene is started again, whatever its state (except if it's still running).
                    // Its configuration is read again.
                    for (var retryId = webOutput.ConsumeRetryRequest(); retryId != null; retryId = webOutput.ConsumeRetryRequest())
                    {
                        var scene = scenes.FirstOrDefault(s => webOutput.GetNodeId(s.Node) == retryId);
                        if (scene == null || webOutput.IsClosing)
                            continue; // (already replaced: ex. two clicks; or closing: nothing new is started)
                        if (scene.Thread?.IsAlive == true)
                        {
                            output.WriteInfo($"Retry: '{GetDisplayName(scene.Sequence)}' is still running, it was not started again.", null, false);
                            continue;
                        }
                        try
                        {
                            // (moved to another folder, ex. from '01-Staging' to 'ManualHQWorkflow-G4': started at its new place, with its configuration)
                            if (!File.Exists(scene.Sequence.ContainerFullPath))
                            {
                                var newPlace = FindNewPlace(scene);
                                if (newPlace == null)
                                {
                                    output.WriteError($"Retry: '{GetDisplayName(scene.Sequence)}' is not there anymore ({scene.Sequence.ContainerFullPath}), and it was not found in another folder (or more than once). Use 'Rescan' to list the scenes on disk.");
                                    continue;
                                }
                                output.WriteInfo($"Retry: '{GetDisplayName(scene.Sequence)}' was moved to '{GetDisplayName(newPlace.Value.Sequence)}'.", null, false);
                                StartScene(newPlace.Value.Sequence, newPlace.Value.Config, scene);
                            }
                            else
                                StartScene(scene.Sequence, TryLoadConfig(scene.Sequence), scene);
                            output.WriteInfo(scene.Thread == null
                                ? $"Retry: '{GetDisplayName(scene.Sequence)}' is still inactive (no enabled worker in its configuration)."
                                : $"Retry: '{GetDisplayName(scene.Sequence)}' started again.", null, false);
                        }
                        catch (Exception ex)
                        {
                            output.WriteError($"Error while starting '{GetDisplayName(scene.Sequence)}' again: {ex.Message}");
                        }
                    }

                    // 'Pause scene' / 'Resume scene' (button of a scene in the page): saved in the .wipsubtitles of the scene, so it's still on hold
                    // when it's loaded again (Retry, Rescan, a restart of the application). Also for a scene that is not running anymore.
                    for (var change = webOutput.ConsumeScenePauseChange(); change != null; change = webOutput.ConsumeScenePauseChange())
                    {
                        var (pauseNodeId, isPaused) = change.Value;
                        var scene = scenes.FirstOrDefault(s => webOutput.GetNodeId(s.Node) == pauseNodeId);
                        var wip = scene?.Context.WIP;
                        if (wip == null || wip.IsOnHold == isPaused)
                            continue; // (its .wipsubtitles is not loaded yet: saved when it's loaded; or nothing to change)
                        try
                        {
                            wip.IsOnHold = isPaused;
                            wip.Save();
                        }
                        catch (Exception ex)
                        {
                            output.WriteError($"Can't save the 'on hold' state of '{GetDisplayName(scene.Sequence)}' in its .wipsubtitles: {ex.Message}");
                        }
                    }

                    // Final report (also on the console: the web page has it in the 'General' log)
                    // (the full report goes in the log file; the page shows it in 'What went wrong' / 'What's next':
                    // the console only gets a summary)
                    void WriteReport()
                    {
                        var reportContext = CreateContext(output);
                        foreach (var scene in scenes)
                        {
                            reportContext.Errors.AddRange(scene.Context.Errors);
                            reportContext.UserTodoList.AddRange(scene.Context.UserTodoList);
                        }
                        WriteFinalReport(reportContext);

                        var nbErrors = reportContext.Errors.Count;
                        var nbTodos = reportContext.UserTodoList.Count + scenes.Count(s => s.Context.BlockedWorkers.Count > 0 && s.Context.Errors.Count == 0);
                        if (nbErrors > 0 || nbTodos > 0)
                        {
                            Console.ForegroundColor = nbErrors > 0 ? ConsoleColor.Red : ConsoleColor.Yellow;
                            Console.WriteLine($"{nbErrors} error(s), {nbTodos} thing(s) to do: see 'What went wrong' / 'What's next' on {webServer.Url}");
                            Console.ResetColor();
                        }
                    }

                    // What went wrong / what's next, by scene (the 2 buttons of the page), always up to date
                    // (the text without the prefix of the scene: the page shows its short name)
                    static string[] WithoutPrefix(IEnumerable<string> lines, string prefix) => lines
                        .Select(line => !string.IsNullOrEmpty(prefix) && line.StartsWith(prefix) ? line.Substring(prefix.Length) : line)
                        .ToArray();
                    webOutput.SetSceneReports(scenes.Select(s =>
                    {
                        string[] errors, todos;
                        lock (s.Context.Errors) { errors = WithoutPrefix(s.Context.Errors, s.Context.Prefix); }
                        lock (s.Context.UserTodoList) { todos = WithoutPrefix(s.Context.UserTodoList, s.Context.Prefix); }
                        return (s.Node, errors, todos);
                    }).ToArray());

                    var isAllDone = scenes.All(s => s.Thread?.IsAlive != true);
                    if (isAllDone && !isReported)
                    {
                        WriteReport();
                        webOutput.SetFinished(true);
                        isReported = true;
                        Console.WriteLine($"All done. The progress UI is still available on {webServer.Url} ('Rescan' can start the new / blocked scenes).");
                        if (!r_options.IsBackgroundChild)
                        {
                            Console.WriteLine(r_options.IsServer
                                ? "Press ENTER here to exit (--runasserver: the page can't close the application)."
                                : "Click on 'Close' in the page, or press ENTER here, to exit.");
                            webServer.StartConsoleExitListener();
                        }
                        // (in the background, there is no console: only 'Close' in the page)
                    }
                    else if (!isAllDone && isReported)
                    {
                        // (a rescan started something)
                        webOutput.SetFinished(false);
                        isReported = false;
                    }

                    // 'Close': right away when nothing is running. Otherwise 'closing' mode: each scene pauses at its next safe point
                    // (between two steps / AI requests: what was received is saved), and the application closes when all the scenes
                    // still alive are paused. 'Force close': right away.
                    var nbAlive = scenes.Count(s => s.Thread?.IsAlive == true);
                    if (webOutput.IsClosing && !webOutput.IsForceClose && nbAlive > 0 && webOutput.NbPausedForClose >= nbAlive)
                    {
                        WriteReport();
                        var message = $"Closed: {nbAlive} scene(s) were paused between two steps (what was done is saved, they'll continue from there on the next run).";
                        rs_log.Info(message);
                        Console.WriteLine(message);
                        break;
                    }
                    if (webOutput.IsForceClose && !isAllDone)
                    {
                        var nbRunning = scenes.Count(s => s.Thread?.IsAlive == true);
                        var message = $"Force close: {nbRunning} scene(s) were still running (what they were doing is lost, the programs they started, ex. ffmpeg, can still be running).";
                        rs_log.Warn(message);
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine(message);
                        Console.ResetColor();
                        break;
                    }
                    if (webOutput.IsClosing && isAllDone)
                        break;

                    webOutput.WaitForRequest(TimeSpan.FromMilliseconds(500));
                }

                return base.NbErrors;
            }
            finally
            {
                webServer.Dispose();
            }
        }

        /// <summary>A scene of the web UI mode: it can be started again by a 'Rescan' (when blocked).</summary>
        private class WebUiScene
        {
            public int Ordinal;
            public VideoSequence Sequence;
            public SubtitleGeneratorContext Context;
            public ITaskNode Node;
            /// <summary>null: inactive (no enabled worker in its configuration, not started).</summary>
            public Thread Thread;
            /// <summary>The configuration it was started with (null: it could not be read then), see the configuration files watcher.</summary>
            public string ConfigHash;
        }

        /// <summary>
        /// The scenes to process, with their merged configuration (creating the .vseq files, if needed).
        /// A scene whose workers are all disabled is in the list too: it's shown as 'inactive' in the UI, and not started
        /// (its .wipsubtitles is not loaded / created).
        /// </summary>
        private List<(VideoSequence Sequence, SubtitleGeneratorConfig Config)> ListVideoSequences()
        {
            return ToVideoSequences(
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
                .Select(vs => (Sequence: vs, Config: TryLoadConfig(vs)))
                .ToList();
        }

        private static string GetDisplayName(VideoSequence videoSequence)
        {
            var wipsubFullpath = Path.ChangeExtension(videoSequence.ContainerFullPath, WorkInProgressSubtitles.Extension);
            return PathExtension.GetRelativePath(Environment.CurrentDirectory, Path.ChangeExtension(Path.GetFullPath(wipsubFullpath), null))?.ToString();
        }

        // A display name template: "[...]" = optional part, "{Name}" = a metadata.
        private static readonly Regex rs_displayNameTemplateRegex = new Regex(@"\[(?<optional>[^\[\]]*)\]|\{(?<name>[^{}]+)\}", RegexOptions.Compiled);
        private static readonly Regex rs_displayNameMetadataRegex = new Regex(@"\{(?<name>[^{}]+)\}", RegexOptions.Compiled);

        /// <summary>
        /// The short name and the metadata of a scene, from 'Global.SceneMetadata' / 'Global.SceneDisplayName' of its merged configuration.
        /// - Not configured, or no pattern matches the path of this scene: the file name.
        /// - A pattern matches, but a metadata used by the display name is missing (or a pattern is invalid): the file name, with an error.
        /// The patterns are applied to the path relative to the folder of the main configuration file (the root of the override files),
        /// ex. "03-REDO\3DSVR-1072\3DSVR-1072- - ....vseq".
        /// </summary>
        private RootNodeInfo BuildRootNodeInfo(VideoSequence videoSequence, SubtitleGeneratorConfig config)
        {
            var fullPath = Path.GetFullPath(videoSequence.ContainerFullPath);
            var configFolder = Path.GetDirectoryName(Path.GetFullPath(r_options.ConfigPath));
            var relativePath = Path.GetRelativePath(configFolder, fullPath);
            var fileName = Path.GetFileNameWithoutExtension(fullPath);
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["FileName"] = fileName };
            var info = new RootNodeInfo { ShortName = fileName, Metadata = metadata };

            var global = config?.Global;
            var anyPatternMatched = false;
            var foundByPattern = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in global?.SceneMetadata ?? Array.Empty<string>())
            {
                Regex regex;
                try
                {
                    regex = new Regex(pattern, RegexOptions.IgnoreCase);
                }
                catch (ArgumentException ex)
                {
                    info.ConfigError = $"Invalid regular expression in 'Global.SceneMetadata': '{pattern}' ({ex.Message}).";
                    return info;
                }

                var match = regex.Match(relativePath);
                if (!match.Success)
                    continue;

                anyPatternMatched = true;
                foreach (var groupName in regex.GetGroupNames().Where(name => !int.TryParse(name, out _)))
                {
                    // The first pattern that finds a metadata wins: the next ones are fallbacks (ex. "ABC-123", else "ABC123").
                    var group = match.Groups[groupName];
                    if (group.Success && !foundByPattern.Contains(groupName))
                    {
                        metadata[groupName] = group.Value;
                        foundByPattern.Add(groupName);
                    }
                }
            }

            if (global?.SceneDisplayName != null && (anyPatternMatched || global.SceneMetadata == null || global.SceneMetadata.Length == 0))
            {
                // "{Name}": required. "[...]": optional part, removed when a metadata used inside is not found (ex. "{ShortName}[ ({Type})]").
                var missing = new List<string>();
                string Expand(string text, List<string> missingNames) => rs_displayNameMetadataRegex.Replace(text, m =>
                {
                    var name = m.Groups["name"].Value;
                    if (metadata.TryGetValue(name, out var value))
                        return value;
                    missingNames.Add(name);
                    return m.Value;
                });
                var displayName = rs_displayNameTemplateRegex.Replace(global.SceneDisplayName, m =>
                {
                    if (m.Groups["optional"].Success)
                    {
                        var missingInOptional = new List<string>();
                        var expanded = Expand(m.Groups["optional"].Value, missingInOptional);
                        return missingInOptional.Count == 0 ? expanded : string.Empty;
                    }
                    return Expand(m.Value, missing);
                }).Trim();

                if (missing.Count > 0)
                {
                    info.ConfigError = $"'Global.SceneDisplayName' ('{global.SceneDisplayName}') uses {string.Join(", ", missing.Select(name => $"'{name}'"))}, "
                        + "not found in the path of some scenes (see 'Global.SceneMetadata'). Their file name is shown instead.";
                }
                else
                {
                    info.ShortName = displayName;
                }
            }

            return info;
        }

        /// <summary>
        /// Reads the merged configuration of an item. Returns null if it cannot be loaded
        /// (the item is then processed normally, so that the loading error is reported as usual).
        /// </summary>
        /// <summary>
        /// The priority of a scene on the shared resources, from 'GlobalShared.ScenePriorities' (the main configuration: the same rules for all
        /// the scenes): the index of the first rule that matches its metadata (0 = the highest), or the number of rules when none matches.
        /// </summary>
        private static void ApplyScenePriority(RootNodeInfo info, ScenePriorityRule[] rules)
        {
            rules ??= Array.Empty<ScenePriorityRule>();
            info.PriorityLevel = rules.Length;
            info.PriorityText = rules.Length == 0 ? null : $"none of the {rules.Length} rules match (after all of them)";
            for (var index = 0; index < rules.Length; index++)
            {
                var rule = rules[index];
                Regex regex;
                try
                {
                    regex = new Regex(rule?.Pattern ?? string.Empty, RegexOptions.IgnoreCase);
                }
                catch (ArgumentException ex)
                {
                    var error = $"Invalid regular expression in 'GlobalShared.ScenePriorities' (rule {index + 1}): '{rule.Pattern}' ({ex.Message}).";
                    info.ConfigError = info.ConfigError == null ? error : info.ConfigError + " " + error;
                    continue;
                }
                if (string.IsNullOrEmpty(rule.Metadata) || info.Metadata == null || !info.Metadata.TryGetValue(rule.Metadata, out var value) || value == null)
                    continue;
                if (regex.IsMatch(value))
                {
                    info.PriorityLevel = index;
                    info.PriorityText = $"{index + 1} of {rules.Length} (rule: {rule.Metadata} ~ '{rule.Pattern}')";
                    return;
                }
            }
        }

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

        private void ProcessSequence(SubtitleGeneratorContext context, VideoSequence videoSequence, SubtitleGeneratorConfig preloadedConfig)
        {
            try
            {
                var watchGlobal = Stopwatch.StartNew();
                // (web UI: the node of this scene, the current task of its thread: for 'Pause scene')
                context.SceneNodeId = TaskScope.Current.Id;
                // (the 'Files' tab of the scene: right away, also for a scene that fails while loading; its backup folder once the
                //  .wipsubtitles is loaded)
                TaskScope.Current.SetFilesView(new Infra.SceneFilesView(context, videoSequence.ContainerFullPath, videoSequence.VideoFullPaths));

                var wipsubFullpath = Path.ChangeExtension(
                    videoSequence.ContainerFullPath,
                    WorkInProgressSubtitles.Extension);

                context.ChangeCurrentFile(null, null);

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

                    // The steps renamed in 3.3 (ex. 'full-ai' => 'transcription-from-ai'): their work kept under the new id
                    var renamedIds = wipsub.RenameOldTrackIds(config.Workers);
                    if (renamedIds.Count > 0)
                    {
                        context.WriteInfo($"Steps renamed (3.3), their work kept: {string.Join(", ", renamedIds)}.");
                        wipsub.Save();
                    }

                    // The data saved under the name of an alias (ex. 'full', when it was a clone): not used anymore (removed at the next save)
                    var aliasIds = new HashSet<string>(Processors.TrackAlias.GetAliases(config).Select(a => a.Id));
                    var nbRemoved = wipsub.Tracks.RemoveAll(t => aliasIds.Contains(t.Id));
                    if (nbRemoved > 0)
                        context.WriteInfo($"{nbRemoved} old copy(ies) of an alias ({string.Join(", ", aliasIds)}) removed from the .wipsubtitles.");
                    context.ChangeCurrentFile(config, wipsub);
                    UpdateWipSubFileIfNeeded(context);

                    // 'Pause scene' is remembered in the .wipsubtitles (on hold): paused again, it waits at its first safe point.
                    // (paused before its .wipsubtitles was loaded, ex. just after a 'Retry': saved now)
                    if (context.SceneNodeId is int sceneNodeId)
                    {
                        if (wipsub.IsOnHold)
                        {
                            context.Output.SetScenePaused(sceneNodeId, true);
                        }
                        else if (context.Output.IsScenePaused(sceneNodeId))
                        {
                            wipsub.IsOnHold = true;
                            wipsub.Save();
                        }
                    }
                });

                if (context.Config.Workers != null)
                {
                    // Shows all the steps in the task tree of the web UI right away (as 'pending')
                    foreach (var worker in context.Config.Workers.Where(w => w.Enabled && w.IsTask))
                    {
                        context.PlanWorkerTask(worker, worker.DisplayName);
                    }

                    // The 'Flow' view of the web UI: the workers of this scene and the data between them
                    // (built from the configuration really used, with the planned tasks: their states come from the tree)
                    try
                    {
                        TaskScope.Current.SetFlowGraph(Infra.SceneFlowGraph.Build(context.Config, context.GetPlannedWorkerTask));
                        // (the items of a box, read when the user clicks on it: from the configuration and the .wipsubtitles of this scene)
                        TaskScope.Current.SetFlowInspector(key => Infra.SceneFlowInspector.Inspect(context, key));
                        // (the number of items of each transcription / translation: in a corner of their box)
                        TaskScope.Current.SetFlowCounter(() => Infra.SceneFlowInspector.CountItems(context));
                        // (the total cost of the AI of the scene, in $: a pill beside its name)
                        TaskScope.Current.SetCostProvider(() => Outputs.OutputCostReport.GetTotalAICost(context.WIP));
                        // (the screenshot sent to the AI for an item: shown when asked, in the tables of the inspector)
                        TaskScope.Current.SetFlowImageProvider((key, field, start, end, onlyIfAvailable) => Infra.SceneFlowInspector.GetImage(context, key, field, start, end, onlyIfAvailable));
                        // (the 'Timing' editor: to revise the timings of the file imported by the scene, ex. '.manual-input.srt')
                        TaskScope.Current.SetTimingEditor(new Infra.SceneTimingEditor(context));
                        // (the 'Config' tab of the inspector: the configuration of a step, read again from the files, with where each value comes from)
                        TaskScope.Current.SetConfigView(new Infra.SceneConfigView(context, r_options.ConfigPath, videoSequence.ContainerFullPath));
                    }
                    catch (Exception ex)
                    {
                        // (only a view: the work continues)
                        rs_log.Error("Can't build the flow graph of the scene.", ex);
                    }

                    foreach (var worker in context.Config.Workers)
                    {
                        // (safe point between two workers: when the application is closing, the next worker is not started)
                        if (worker.Enabled && worker.IsTask)
                            context.PauseIfClosing();
                        // (to be started again ('Redo this step'): no other worker, not even the ones that are not a task)
                        context.ThrowIfRestartRequested();
                        worker.Execute(context);
                    }
                }

                context.WriteInfo($"Finished in {watchGlobal.Elapsed}.");
                context.WriteInfo();
            }
            catch (Infra.SceneRestartException)
            {
                // ('Redo this step' while it was running or on hold: ended at a safe point, the main loop starts it again)
                context.WriteInfo("Ended at a safe point: the scene is started again.");
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