using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FunscriptToolbox.SubtitlesVerbs.Progress
{
    /// <summary>
    /// Keeps the tree of tasks in memory so a web page can display it (see <see cref="WebUiServer"/>).
    /// Everything is thread-safe: sequences are processed in parallel, each on its own thread.
    /// </summary>
    public class WebUiOutput : IUserOutput
    {
        private const int MaxLogLinesPerNode = 5000;
        private const int MaxDetailChars = 1_500_000;
        private static readonly Regex rs_base64Regex = new Regex(@"[A-Za-z0-9+/=]{400,}", RegexOptions.Compiled);

        internal readonly object Gate = new object();
        private readonly List<UiNode> m_roots = new List<UiNode>();
        private readonly Dictionary<int, UiNode> m_nodesById = new Dictionary<int, UiNode>();
        private readonly List<PendingPrompt> m_prompts = new List<PendingPrompt>();
        private readonly UiNode m_general;
        private int m_nextId = 0;
        private int m_nextPromptId = 0;
        private long m_version = 0;

        public SharedLocks Locks { get; }
        public bool IsFinished { get; private set; }

        public WebUiOutput(SharedLocks locks)
        {
            this.Locks = locks;
            m_general = new UiNode(this, null, "General", "General");
            m_general.StartInternal(setAsCurrent: false);
        }


        internal long Version => Interlocked.Read(ref m_version);
        internal void Touch() => Interlocked.Increment(ref m_version);

        /// <summary>True when all the scenes are done (false again when a 'Rescan' starts something).</summary>
        public void SetFinished(bool isFinished)
        {
            lock (this.Gate)
            {
                this.IsFinished = isFinished;
                Touch();
            }
        }

        // ---- 'Rescan' (asked from the page, done by the main thread) ----

        private readonly ManualResetEventSlim r_requestReceived = new ManualResetEventSlim(false);
        private int m_rescanRequested;

        internal void RequestRescan()
        {
            if (IsClosing)
                return; // (closing: nothing new is started)
            Interlocked.Exchange(ref m_rescanRequested, 1);
            // (shown right away in the page: the scan of the disk can take a few seconds)
            SetRescanState(isRescanning: true, result: null);
            r_requestReceived.Set();
        }

        // ---- What went wrong / what's next, by scene (for the 2 buttons of the page) ----

        private object[] m_sceneReports = Array.Empty<object>();

        /// <summary>The errors and the 'to do' of each scene (only the scenes that have some), for the 'What went wrong' / 'What's next' views.</summary>
        public void SetSceneReports(IEnumerable<(ITaskNode Node, string[] Errors, string[] Todos)> reports)
        {
            var list = reports
                .Where(r => r.Errors.Length > 0 || r.Todos.Length > 0)
                .Select(r => (object)new { sceneId = (r.Node as UiNode)?.Id, errors = r.Errors, todos = r.Todos })
                .ToArray();
            var changed = JsonConvertForCompare(list) != JsonConvertForCompare(m_sceneReports);
            lock (this.Gate)
            {
                m_sceneReports = list;
                if (changed) Touch();
            }
        }

        private static string JsonConvertForCompare(object value) => Newtonsoft.Json.JsonConvert.SerializeObject(value);

        /// <summary>True from the click on 'Rescan' until the scenes on disk are listed again (and the new / blocked ones started).</summary>
        public bool IsRescanning { get; private set; }

        /// <summary>What the last 'Rescan' did (ex. "2 new scene(s) started, 1 blocked scene(s) checked again"), shown for a moment in the page.</summary>
        public string LastRescanResult { get; private set; }
        private long m_lastRescanResultMs;

        public void SetRescanState(bool isRescanning, string result)
        {
            lock (this.Gate)
            {
                IsRescanning = isRescanning;
                if (result != null)
                {
                    LastRescanResult = result;
                    m_lastRescanResultMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
                Touch();
            }
        }

        /// <summary>Wakes up the main thread waiting in <see cref="WaitForRequest"/> (ex. 'Close' was clicked).</summary>
        internal void RequestWakeUp() => r_requestReceived.Set();

        // ---- 'Close' ----

        /// <summary>
        /// 'Closing' mode: the application closes when the running scenes are finished, and nothing new is started
        /// meanwhile ('Rescan' and 'Retry' are refused). The user can leave this mode ('Don't close').
        /// </summary>
        public bool IsClosing { get; private set; }

        /// <summary>The user confirmed to close right away, even if some scenes are still running.</summary>
        public bool IsForceClose { get; private set; }

        /// <summary>
        /// --runasserver: the page has no 'Close' button (its close requests are refused), only 'Pause all' / 'Resume all'.
        /// </summary>
        public bool IsServer { get; set; }

        /// <summary>False as a server: the page can't close the application.</summary>
        public bool AllowCloseFromPage => !IsServer;

        /// <summary>'Pause all' (server): the scenes pause at their next safe point, until 'Resume all'. The application stays open.</summary>
        public bool IsPausedAll { get; private set; }

        internal void SetPausedAll(bool isPaused)
        {
            lock (this.Gate)
            {
                IsPausedAll = isPaused;
                Touch();
            }
        }

        public string PauseAtSafePointReason =>
            IsClosing ? "paused: the application closes when the running steps are finished"
            : IsPausedAll ? "paused: 'Pause all' (click on 'Resume all' to continue)"
            : null;

        /// <summary>The scenes paused by the user ('Pause scene'), by their name: still paused when they are started again (Rescan, Retry).</summary>
        private readonly HashSet<string> r_pausedScenes = new HashSet<string>();

        /// <summary>
        /// 'Pause scene' / 'Resume scene' (rootNodeId: the node of the scene). False: unknown scene.
        /// The change is also queued for the main thread, which saves it in the .wipsubtitles of the scene (see <see cref="ConsumeScenePauseChange"/>).
        /// </summary>
        public bool SetScenePaused(int rootNodeId, bool isPaused)
        {
            lock (this.Gate)
            {
                if (!m_nodesById.TryGetValue(rootNodeId, out var node) || node.Parent != null)
                    return false;
                if (isPaused) r_pausedScenes.Add(node.Name); else r_pausedScenes.Remove(node.Name);
                Touch();
            }
            r_scenePauseChanges.Enqueue((rootNodeId, isPaused));
            r_requestReceived.Set();
            return true;
        }

        /// <summary>The scene (rootNodeId: the node of the scene) is paused by the user ('Pause scene').</summary>
        public bool IsScenePaused(int rootNodeId)
        {
            lock (this.Gate)
            {
                return m_nodesById.TryGetValue(rootNodeId, out var node) && r_pausedScenes.Contains(node.Name);
            }
        }

        private readonly System.Collections.Concurrent.ConcurrentQueue<(int RootNodeId, bool IsPaused)> r_scenePauseChanges
            = new System.Collections.Concurrent.ConcurrentQueue<(int, bool)>();

        /// <summary>The next 'Pause scene' / 'Resume scene' to save in the .wipsubtitles of the scene (done by the main thread), or null.</summary>
        public (int RootNodeId, bool IsPaused)? ConsumeScenePauseChange()
            => r_scenePauseChanges.TryDequeue(out var change) ? change : ((int, bool)?)null;

        public string GetPauseAtSafePointReason(int? sceneNodeId)
        {
            var reason = PauseAtSafePointReason;
            if (reason != null || sceneNodeId == null)
                return reason;
            lock (this.Gate)
            {
                return m_nodesById.TryGetValue(sceneNodeId.Value, out var node) && r_pausedScenes.Contains(node.Name)
                    ? "on hold: 'Pause scene', saved in its .wipsubtitles (click on 'Resume scene' to continue)"
                    : null;
            }
        }

        internal void RequestClose(bool force)
        {
            lock (this.Gate)
            {
                IsClosing = true;
                IsForceClose |= force;
                Touch();
            }
            r_requestReceived.Set();
        }

        private int m_nbPausedForClose;

        /// <summary>How many scenes are paused at a safe point, waiting for the application to close.</summary>
        public int NbPausedForClose => Volatile.Read(ref m_nbPausedForClose);

        public IDisposable EnterPauseForClose()
        {
            Interlocked.Increment(ref m_nbPausedForClose);
            Touch();
            r_requestReceived.Set(); // (the main thread checks if all the scenes are paused)
            return new PauseReleaser(this);
        }

        private sealed class PauseReleaser : IDisposable
        {
            private WebUiOutput m_owner;
            public PauseReleaser(WebUiOutput owner) { m_owner = owner; }
            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref m_owner, null);
                if (owner != null)
                {
                    Interlocked.Decrement(ref owner.m_nbPausedForClose);
                    owner.Touch();
                }
            }
        }

        /// <summary>Leaves the 'closing' mode (not possible after a force close).</summary>
        internal void CancelClose()
        {
            lock (this.Gate)
            {
                if (!IsForceClose) IsClosing = false;
                Touch();
            }
        }

        /// <summary>True once for each 'Rescan' asked by the user.</summary>
        public bool ConsumeRescanRequest() => Interlocked.Exchange(ref m_rescanRequested, 0) == 1;

        // ---- 'Retry' of one scene (asked from the page, done by the main thread) ----

        private readonly System.Collections.Concurrent.ConcurrentQueue<int> r_retryRequests = new System.Collections.Concurrent.ConcurrentQueue<int>();

        internal bool RequestRetry(int rootNodeId)
        {
            lock (this.Gate)
            {
                if (IsClosing)
                    return false; // (closing: nothing new is started)
                if (!m_nodesById.TryGetValue(rootNodeId, out var node) || node.Parent != null || !m_roots.Contains(node))
                    return false;
            }
            r_retryRequests.Enqueue(rootNodeId);
            r_requestReceived.Set();
            return true;
        }

        /// <summary>The id of the first-level node (scene) that the user asked to start again, or null.</summary>
        public int? ConsumeRetryRequest() => r_retryRequests.TryDequeue(out var id) ? id : (int?)null;

        /// <summary>The id of a node in the page (null: not a node of the web UI).</summary>
        public int? GetNodeId(ITaskNode node) => (node as UiNode)?.Id;

        /// <summary>Waits for a request from the page (ex. 'Rescan'), at most for the given time.</summary>
        public void WaitForRequest(TimeSpan timeout)
        {
            if (r_requestReceived.Wait(timeout))
            {
                r_requestReceived.Reset();
            }
        }

        /// <summary>
        /// Replaces a first-level node that is finished by a new one, at the same place in the list (ex. a blocked scene
        /// started again by a 'Rescan'). The old node and all its sub-tasks are forgotten.
        /// </summary>
        public ITaskNode ReplaceRootNode(ITaskNode oldNode, string kind, string name, RootNodeInfo info = null)
        {
            lock (this.Gate)
            {
                var old = oldNode as UiNode;
                var index = old != null ? m_roots.IndexOf(old) : -1;
                if (old != null)
                {
                    foreach (var forgotten in old.GetSelfAndDescendants())
                    {
                        m_nodesById.Remove(forgotten.Id);
                    }
                }

                var node = new UiNode(this, null, kind, name) { Info = info };
                if (index >= 0)
                    m_roots[index] = node;
                else
                    m_roots.Add(node);
                Touch();
                return node;
            }
        }

        public ITaskNode CreateRootNode(string kind, string name, RootNodeInfo info = null)
        {
            lock (this.Gate)
            {
                var node = new UiNode(this, null, kind, name) { Info = info };
                m_roots.Add(node);
                return node;
            }
        }

        private UiNode CurrentNode => TaskScope.Current as UiNode ?? m_general;

        public void WriteInfo(string message, ConsoleColor? color, bool isProgress)
        {
            if (isProgress)
            {
                CurrentNode.SetStatus(message?.Trim());
            }
            else if (!string.IsNullOrWhiteSpace(message))
            {
                // (an empty line: the spacing of the old console output, not shown; the page separates the tasks by itself)
                CurrentNode.AddLogLine(message, color);
            }
        }

        public void WriteVerbose(string message, ConsoleColor? color, bool isProgress, bool isVerboseEnabled)
        {
            if (isProgress)
            {
                CurrentNode.SetStatus(message?.Trim());
            }
            else if (isVerboseEnabled && !string.IsNullOrWhiteSpace(message))
            {
                CurrentNode.AddLogLine(message, color);
            }
        }

        public void WriteError(string message)
        {
            // (only in the page: 'What went wrong'. They are also in the log file. The console only gets a summary at the end)
            CurrentNode.AddLogLine(message, ConsoleColor.Red, isError: true);
        }

        // The text streamed by the AIs goes in the log of the task (as lines, the last one can be incomplete).
        public void WriteStream(string text)
        {
            if (!string.IsNullOrEmpty(text)) CurrentNode.AppendStream(text);
        }

        public void WriteStreamLine(string text)
        {
            CurrentNode.AppendStream((text ?? string.Empty) + "\n");
        }

        public bool PollStopRequest() => CurrentNode.ConsumeCancelRequest();

        public UserAnswer Ask(UserPrompt prompt)
        {
            var node = CurrentNode;
            var pending = new PendingPrompt { Node = node, Prompt = prompt };
            lock (this.Gate)
            {
                pending.Id = ++m_nextPromptId;
                m_prompts.Add(pending);
                Touch();
            }
            node.SetState(TaskState.WaitingForUser, prompt.Title);
            try
            {
                pending.Done.Wait();
            }
            finally
            {
                lock (this.Gate)
                {
                    m_prompts.Remove(pending);
                    Touch();
                }
                node.SetState(TaskState.Running);
            }
            return pending.Answer;
        }

        // ---- Used by the web server ----

        internal bool AnswerPrompt(int promptId, string choice, string text)
        {
            PendingPrompt pending;
            lock (this.Gate)
            {
                pending = m_prompts.FirstOrDefault(p => p.Id == promptId);
            }
            if (pending == null || pending.Done.IsSet) return false;
            pending.Answer = new UserAnswer { Choice = choice, Text = text };
            pending.Done.Set();
            return true;
        }

        /// <summary>
        /// A Stop button of a task: 'now' (right away) or 'skip' (the button of ITaskNode.SetSkippable).
        /// ('Pause after this request' is 'Pause scene', see SetScenePaused.)
        /// </summary>
        internal bool RequestCancel(int nodeId, string mode)
        {
            lock (this.Gate)
            {
                if (!m_nodesById.TryGetValue(nodeId, out var node)) return false;
                switch (mode)
                {
                    case "now": node.RequestCancelInternal(); return true;
                    case "skip": return node.RequestSkipInternal();
                    default: return false;
                }
            }
        }

        internal object CreateSnapshot()
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (this.Gate)
            {
                return new
                {
                    version = Version,
                    nowMs,
                    finished = IsFinished,
                    closing = IsClosing,
                    canClose = AllowCloseFromPage,
                    server = IsServer,
                    pausedAll = IsPausedAll,
                    pausedScenes = r_pausedScenes.ToArray(),
                    rescanning = IsRescanning,
                    sceneReports = m_sceneReports,
                    // The errors that are not about a scene (ex. a configuration problem), shown in 'What went wrong'
                    generalErrors = m_general.GetErrorLines(),
                    lastRescan = LastRescanResult == null ? null : new { text = LastRescanResult, ms = m_lastRescanResultMs },
                    pausedForClose = NbPausedForClose,
                    // (the scenes in their order of the list: a scene started again keeps its place)
                    rootOrder = m_roots.Select(r => r.Id).ToArray(),
                    nodes = m_nodesById.Values.OrderBy(n => n.Id).Select(n => n.ToSnapshot()).ToArray(),
                    prompts = m_prompts.Select(p => new
                    {
                        id = p.Id,
                        nodeId = p.Node.Id,
                        nodePath = p.Node.GetPath(),
                        title = p.Prompt.Title,
                        message = p.Prompt.Message,
                        editableText = p.Prompt.EditableText,
                        choices = p.Prompt.Choices
                    }).ToArray(),
                    locks = this.Locks.All.Select(l =>
                    {
                        // (the lock state is copied under its own lock: no node is called while it is held)
                        var (holders, waiters) = l.GetUsage();
                        return new
                        {
                            name = l.Name,
                            shortName = l.ShortName,
                            description = l.Description,
                            max = l.MaxConcurrent,
                            holders = holders.Select(h => new { nodeId = (h.Node as UiNode)?.Id, description = h.Description }).ToArray(),
                            waiters = waiters.Select(w => new { nodeId = (w.Node as UiNode)?.Id, description = w.Description }).ToArray()
                        };
                    }).ToArray()
                };
            }
        }

        /// <summary>The items of a box of the 'Flow' graph of a scene (null: unknown scene, or no inspector).</summary>
        internal object InspectFlow(int sceneNodeId, string key)
        {
            Func<string, object> inspector;
            lock (this.Gate)
            {
                if (!m_nodesById.TryGetValue(sceneNodeId, out var node)) return null;
                inspector = node.FlowInspector;
            }
            if (inspector == null) return null;

            // (called without the gate: it reads the .wipsubtitles of the scene, the scene can be using it and writing in the page)
            try
            {
                return inspector(key);
            }
            catch (Exception ex)
            {
                return new { title = key, note = $"Can't read it right now: {ex.Message}", sections = Array.Empty<object>() };
            }
        }

        /// <summary>The screenshot sent to the AI for an item, by a worker of the 'Flow' graph of a scene (null: none).</summary>
        internal byte[] GetFlowImage(int sceneNodeId, string workerKey, string field, TimeSpan start, TimeSpan end, bool onlyIfAvailable)
        {
            Func<string, string, TimeSpan, TimeSpan, bool, byte[]> provider;
            lock (this.Gate)
            {
                // (not a node anymore: the scene was started again, ex. 'Retry', the page still asks with the id of the old node)
                if (!m_nodesById.TryGetValue(sceneNodeId, out var node))
                    throw new InvalidOperationException("the scene was started again (Retry / Rescan): click on the box again in its graph.");
                provider = node.FlowImageProvider;
            }
            // (called without the gate: it can take a screenshot with ffmpeg, and wait for the ffmpeg lock of the video)
            return provider?.Invoke(workerKey, field, start, end, onlyIfAvailable);
        }

        /// <summary>The data of the 'Timing' editor of a scene (null: unknown scene, or no editor). Its methods are called without the gate.</summary>
        internal ITimingEditor GetTimingEditor(int sceneNodeId)
        {
            lock (this.Gate)
            {
                return m_nodesById.TryGetValue(sceneNodeId, out var node) ? node.TimingEditor : null;
            }
        }

        internal object CreateNodeDetail(int nodeId)
        {
            // A scene: the number of items of the boxes of its graph (read without the gate: it reads the .wipsubtitles of the scene)
            // (and the total cost of the AI of its scene, for any task of the scene: a pill beside its name)
            Func<object> counter;
            Func<double?> costProvider;
            lock (this.Gate)
            {
                if (!m_nodesById.TryGetValue(nodeId, out var found)) return null;
                counter = found.FlowCounter;
                var root = found;
                while (root.Parent != null) root = root.Parent;
                costProvider = root.CostProvider;
            }
            object flowCounts = null;
            if (counter != null)
            {
                try { flowCounts = counter(); } catch (Exception) { /* (only an indicator: not shown this time) */ }
            }
            double? sceneCost = null;
            if (costProvider != null)
            {
                try { sceneCost = costProvider(); } catch (Exception) { /* (only an indicator: not shown this time) */ }
            }

            lock (this.Gate)
            {
                if (!m_nodesById.TryGetValue(nodeId, out var node)) return null;
                return node.ToDetail(flowCounts, sceneCost);
            }
        }

        private class PendingPrompt
        {
            public int Id;
            public UiNode Node;
            public UserPrompt Prompt;
            public UserAnswer Answer;
            public ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private static long? ToMs(DateTime? value) => value == null ? (long?)null : new DateTimeOffset(value.Value).ToUnixTimeMilliseconds();

        private class LogLine
        {
            public long TimeMs;
            public string Text;
            public string Color;
            public bool IsError;
            /// <summary>Text streamed by an AI (its response, shown a bit lighter).</summary>
            public bool IsStream;
        }

        private const int MaxCombinedLogLines = 5000;

        /// <summary>The kinds of tasks whose start / end are shown in the combined log of a parent task (the 'high level' view).</summary>
        private static readonly HashSet<string> rs_kindsWithEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Worker", "AI request", "Background" };

        private class DetailContent
        {
            public StringBuilder Content = new StringBuilder();
            public bool Truncated;
        }

        private sealed class UiNode : ITaskNode
        {
            private readonly WebUiOutput r_owner;
            private readonly List<UiNode> r_children = new List<UiNode>();
            private readonly List<LogLine> r_log = new List<LogLine>();
            private readonly Dictionary<string, DetailContent> r_details = new Dictionary<string, DetailContent>();
            private ITaskNode m_previousCurrent;
            private bool m_isCurrent;

            public int Id { get; }
            public UiNode Parent { get; }
            public string Kind { get; }
            public string Name { get; }
            public TaskState State { get; private set; } = TaskState.Pending;
            public string Reason { get; private set; }
            public string Status { get; private set; }
            public DateTime? StartedAt { get; private set; }
            public DateTime? EndedAt { get; private set; }
            public string CancelHint { get; private set; }
            public int CancelRequests { get; private set; }
            private int m_cancelConsumed;
            /// <summary>'Pause after this request' is offered (an AI request): the page shows it, it's 'Pause scene' (see SetScenePaused).</summary>
            public bool CanPauseAfter { get; private set; }
            /// <summary>A 'Stop now' changed into a stop after this request (see ConvertToStopAfter).</summary>
            private volatile bool m_stopAfterRequested;
            public bool IsStopAfterRequested => m_stopAfterRequested;
            /// <summary>The button of <see cref="SetSkippable"/> (null: not offered) and its tooltip.</summary>
            public string SkipLabel { get; private set; }
            public string SkipHint { get; private set; }
            private volatile bool m_skipRequested;
            public bool IsSkipRequested => m_skipRequested;
            public string StopNowWarning { get; private set; }
            public int ErrorCount { get; private set; }
            public bool IsBackground { get; }
            /// <summary>Only for the first-level nodes (videos): short name, metadata.</summary>
            public RootNodeInfo Info { get; set; }
            private long m_progressDone;
            private long? m_progressTotal;
            private string m_progressUnit;

            public UiNode(WebUiOutput owner, UiNode parent, string kind, string name, bool isBackground = false)
            {
                r_owner = owner;
                Parent = parent;
                Kind = kind;
                Name = name;
                IsBackground = isBackground;
                lock (owner.Gate)
                {
                    Id = ++owner.m_nextId;
                    owner.m_nodesById[Id] = this;
                    parent?.r_children.Add(this);
                    owner.Touch();
                }
            }

            private bool IsTerminal => State.IsTerminal();

            public string GetPath() => Parent == null ? Name : $"{Parent.GetPath()} > {Name}";

            public ITaskNode AddPendingChild(string kind, string name, bool isBackground = false) => new UiNode(r_owner, this, kind, name, isBackground);

            public void Start() => StartInternal(setAsCurrent: true);

            internal void StartInternal(bool setAsCurrent)
            {
                lock (r_owner.Gate)
                {
                    if (State == TaskState.Pending)
                    {
                        State = TaskState.Running;
                        StartedAt = DateTime.UtcNow;
                    }
                    r_owner.Touch();
                }
                if (setAsCurrent)
                {
                    m_previousCurrent = TaskScope.Current;
                    m_isCurrent = true;
                    TaskScope.Current = this;
                }
            }

            public void SetState(TaskState state, string reason = null)
            {
                lock (r_owner.Gate)
                {
                    if (IsTerminal) return;
                    if (state.IsTerminal())
                    {
                        if (StartedAt == null) StartedAt = DateTime.UtcNow;
                        Finish(state, reason);
                        return;
                    }
                    State = state;
                    Reason = reason;
                    if (state == TaskState.Running && StartedAt == null) StartedAt = DateTime.UtcNow;
                    r_owner.Touch();
                }
            }

            public void SetStatus(string text)
            {
                lock (r_owner.Gate)
                {
                    if (Status == text) return;
                    Status = text;
                    r_owner.Touch();
                }
            }

            public void SetProgress(long done, long? total, string unit = null)
            {
                lock (r_owner.Gate)
                {
                    m_progressDone = done;
                    m_progressTotal = total;
                    if (unit != null) m_progressUnit = unit;
                    r_owner.Touch();
                }
            }

            public void IncrementProgress(long by = 1)
            {
                lock (r_owner.Gate)
                {
                    m_progressDone += by;
                    r_owner.Touch();
                }
            }

            private readonly StringBuilder r_streamPartial = new StringBuilder();
            private long m_streamPartialTimeMs;

            /// <summary>Streamed text: each complete line becomes a log line (the incomplete one is shown at the end of the log).</summary>
            public void AppendStream(string text)
            {
                lock (r_owner.Gate)
                {
                    if (r_streamPartial.Length == 0) m_streamPartialTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    r_streamPartial.Append(text);
                    var content = r_streamPartial.ToString();
                    var lastNewLine = content.LastIndexOf('\n');
                    if (lastNewLine >= 0)
                    {
                        foreach (var line in content.Substring(0, lastNewLine).Split('\n'))
                        {
                            AddLogLineInternal(line.TrimEnd('\r'), null, false, isStream: true);
                        }
                        r_streamPartial.Clear();
                        r_streamPartial.Append(content.Substring(lastNewLine + 1));
                        m_streamPartialTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    }
                    r_owner.Touch();
                }
            }

            public void AddLogLine(string text, ConsoleColor? color = null, bool isError = false)
            {
                lock (r_owner.Gate)
                {
                    AddLogLineInternal(text, color, isError, isStream: false);
                }
            }

            private void AddLogLineInternal(string text, ConsoleColor? color, bool isError, bool isStream)
            {
                // Called with the gate held.
                {
                    r_log.Add(new LogLine
                    {
                        TimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Text = text ?? string.Empty,
                        Color = color?.ToString(),
                        IsError = isError,
                        IsStream = isStream
                    });
                    if (r_log.Count > MaxLogLinesPerNode) r_log.RemoveRange(0, r_log.Count - MaxLogLinesPerNode);
                    if (isError)
                    {
                        for (var n = this; n != null; n = n.Parent) n.ErrorCount++;
                    }
                    r_owner.Touch();
                }
            }

            public void SetDetail(string name, string content)
            {
                var elided = rs_base64Regex.Replace(content ?? string.Empty, m => $"<{m.Length} chars of binary data (base64) not shown>");
                lock (r_owner.Gate)
                {
                    var detail = new DetailContent();
                    Append(detail, elided);
                    r_details[name] = detail;
                    r_owner.Touch();
                }
            }

            public void AppendDetail(string name, string content)
            {
                lock (r_owner.Gate)
                {
                    if (!r_details.TryGetValue(name, out var detail))
                    {
                        detail = new DetailContent();
                        r_details[name] = detail;
                    }
                    Append(detail, content);
                    r_owner.Touch();
                }
            }

            private static void Append(DetailContent detail, string text)
            {
                if (detail.Truncated || string.IsNullOrEmpty(text)) return;
                var room = MaxDetailChars - detail.Content.Length;
                if (text.Length > room)
                {
                    detail.Content.Append(text, 0, Math.Max(0, room));
                    detail.Content.Append("\n[... truncated ...]");
                    detail.Truncated = true;
                }
                else
                {
                    detail.Content.Append(text);
                }
            }

            public void SetCancellable(string hint)
            {
                lock (r_owner.Gate)
                {
                    CancelHint = hint;
                    if (hint == null)
                    {
                        // Not cancellable anymore: a click that was not consumed must not stop something else later.
                        m_cancelConsumed = CancelRequests;
                    }
                    r_owner.Touch();
                }
            }

            internal void RequestCancelInternal()
            {
                // Called with the gate held.
                if (CancelRequests > m_cancelConsumed)
                    return; // (already asked, not handled yet: a second click changes nothing)
                CancelRequests++;
                AddLogLine("'Stop now' requested by the user.");
            }

            public void ConvertToStopAfter(string reason)
            {
                lock (r_owner.Gate)
                {
                    m_stopAfterRequested = true;
                    AddLogLine($"'Stop now' changed to a stop after this request: {reason}");
                    r_owner.Touch();
                }
            }

            public void SetSkippable(string label, string hint)
            {
                lock (r_owner.Gate)
                {
                    SkipLabel = label;
                    SkipHint = label == null ? null : hint;
                    r_owner.Touch();
                }
            }

            internal bool RequestSkipInternal()
            {
                // Called with the gate held.
                if (SkipLabel == null || IsTerminal || m_skipRequested)
                    return false;
                m_skipRequested = true;
                AddLogLine($"'{SkipLabel}' requested by the user.");
                return true;
            }

            public void SetPauseAfterSupported()
            {
                lock (r_owner.Gate)
                {
                    CanPauseAfter = true;
                    r_owner.Touch();
                }
            }

            public void SetStopNowWarning(string warning)
            {
                lock (r_owner.Gate)
                {
                    if (StopNowWarning == warning) return;
                    StopNowWarning = warning;
                    r_owner.Touch();
                }
            }

            public bool ConsumeCancelRequest()
            {
                lock (r_owner.Gate)
                {
                    for (var n = this; n != null; n = n.Parent)
                    {
                        if (n.CancelRequests > n.m_cancelConsumed)
                        {
                            n.m_cancelConsumed++;
                            r_owner.Touch();
                            return true;
                        }
                    }
                    return false;
                }
            }

            int? ITaskNode.Id => Id;

            /// <summary>See <see cref="ITaskNode.SetFlowGraph"/> (sent with the details of the node).</summary>
            private object m_flowGraph;

            public void SetFlowGraph(object graph)
            {
                lock (r_owner.Gate)
                {
                    m_flowGraph = graph;
                    r_owner.Touch();
                }
            }

            /// <summary>See <see cref="ITaskNode.SetFlowInspector"/>.</summary>
            internal Func<string, object> FlowInspector { get; private set; }

            public void SetFlowInspector(Func<string, object> inspector)
            {
                lock (r_owner.Gate)
                {
                    FlowInspector = inspector;
                }
            }

            /// <summary>See <see cref="ITaskNode.SetFlowImageProvider"/>.</summary>
            internal Func<string, string, TimeSpan, TimeSpan, bool, byte[]> FlowImageProvider { get; private set; }

            public void SetFlowImageProvider(Func<string, string, TimeSpan, TimeSpan, bool, byte[]> provider)
            {
                lock (r_owner.Gate)
                {
                    FlowImageProvider = provider;
                }
            }

            /// <summary>See <see cref="ITaskNode.SetTimingEditor"/>.</summary>
            internal ITimingEditor TimingEditor { get; private set; }

            public void SetTimingEditor(ITimingEditor editor)
            {
                lock (r_owner.Gate)
                {
                    TimingEditor = editor;
                }
            }

            /// <summary>See <see cref="ITaskNode.SetFlowCounter"/>.</summary>
            internal Func<object> FlowCounter { get; private set; }

            public void SetFlowCounter(Func<object> counter)
            {
                lock (r_owner.Gate)
                {
                    FlowCounter = counter;
                }
            }

            /// <summary>See <see cref="ITaskNode.SetCostProvider"/>.</summary>
            internal Func<double?> CostProvider { get; private set; }

            public void SetCostProvider(Func<double?> provider)
            {
                lock (r_owner.Gate)
                {
                    CostProvider = provider;
                }
            }

            /// <summary>See <see cref="ITaskNode.MarkAsRefreshOnly"/>.</summary>
            public bool IsRefreshOnly { get; private set; }

            public void MarkAsRefreshOnly()
            {
                lock (r_owner.Gate)
                {
                    IsRefreshOnly = true;
                    Reason ??= "refresh only (CanBeUpdated)";
                    r_owner.Touch();
                }
            }

            /// <summary>See <see cref="ITaskNode.SetUserAction"/>.</summary>
            public string UserAction { get; private set; }

            public void SetUserAction(string action)
            {
                lock (r_owner.Gate)
                {
                    if (UserAction == action) return;
                    UserAction = action;
                    r_owner.Touch();
                }
            }

            public void Fail(string message)
            {
                lock (r_owner.Gate)
                {
                    if (!string.IsNullOrEmpty(message)) AddLogLine(message, ConsoleColor.Red, isError: true);
                    Finish(TaskState.Failed, message);
                }
            }

            private void Finish(TaskState finalState, string reason)
            {
                // Called with the gate held.
                if (IsTerminal) return;
                State = finalState;
                Reason = reason ?? Reason;
                EndedAt = DateTime.UtcNow;
                CancelHint = null;
                foreach (var child in r_children)
                {
                    // What was planned but never started
                    if (child.State == TaskState.Pending)
                    {
                        child.State = TaskState.Skipped;
                        child.Reason = "not reached";
                    }
                }
                r_owner.Touch();
            }

            public void Dispose()
            {
                lock (r_owner.Gate)
                {
                    if (State != TaskState.Pending)
                    {
                        Finish(TaskState.Succeeded, null);
                        EndedAt ??= DateTime.UtcNow;
                    }
                }
                if (m_isCurrent)
                {
                    m_isCurrent = false;
                    TaskScope.Current = m_previousCurrent;
                }
            }

            public object ToSnapshot()
            {
                return new
                {
                    id = Id,
                    parentId = Parent?.Id,
                    kind = Kind,
                    name = Name,
                    state = State.ToString(),
                    reason = Reason,
                    status = Status,
                    startedMs = ToMs(StartedAt),
                    endedMs = ToMs(EndedAt),
                    errors = ErrorCount,
                    cancelHint = CancelHint,
                    stopNowRequested = CancelRequests > m_cancelConsumed,
                    canPauseAfter = CanPauseAfter && !IsTerminal,
                    stopAfter = m_stopAfterRequested,
                    skipLabel = IsTerminal ? null : SkipLabel,
                    skipHint = SkipHint,
                    skipRequested = m_skipRequested,
                    stopWarning = StopNowWarning,
                    background = IsBackground,
                    progressDone = (m_progressTotal != null || m_progressDone > 0) ? m_progressDone : (long?)null,
                    progressTotal = m_progressTotal,
                    progressUnit = m_progressUnit,
                    refreshOnly = IsRefreshOnly,
                    userAction = UserAction,
                    logLines = r_log.Count,
                    shortName = Info?.ShortName,
                    meta = Info?.Metadata,
                    priority = Info?.PriorityText,
                    inactive = Info?.IsInactive == true,
                    configError = Info?.ConfigError
                };
            }

            public object ToDetail(object flowCounts = null, double? sceneCost = null)
            {
                // Called with the gate held.
                // A task with sub-tasks: its log is combined with the log of all its sub-tasks (including the responses streamed by the AIs),
                // with a line when an important sub-task starts / ends (worker, AI request, background task): a 'high level' view.
                var lines = new List<(long t, long order, object line)>();
                long order = 0;
                foreach (var l in r_log)
                {
                    lines.Add((l.TimeMs, order++, new { t = l.TimeMs, text = l.Text, color = l.Color, error = l.IsError, stream = l.IsStream }));
                }
                if (r_streamPartial.Length > 0)
                {
                    lines.Add((m_streamPartialTimeMs, order++, new { t = m_streamPartialTimeMs, text = r_streamPartial.ToString(), stream = true }));
                }
                foreach (var descendant in GetDescendants())
                {
                    var task = descendant.GetPathFrom(this);
                    foreach (var l in descendant.r_log)
                    {
                        lines.Add((l.TimeMs, order++, new { t = l.TimeMs, text = l.Text, color = l.Color, error = l.IsError, stream = l.IsStream, task }));
                    }
                    if (descendant.r_streamPartial.Length > 0)
                    {
                        // (the line of the response that is being received)
                        lines.Add((descendant.m_streamPartialTimeMs, order++, new { t = descendant.m_streamPartialTimeMs, text = descendant.r_streamPartial.ToString(), stream = true, task }));
                    }
                    foreach (var ev in descendant.GetEvents())
                    {
                        lines.Add((ev.t, order++, new { t = ev.t, text = ev.text, ev = ev.kind, task }));
                    }
                }

                return new
                {
                    id = Id,
                    path = GetPath(),
                    combined = r_children.Count > 0,
                    flow = m_flowGraph,
                    flowCounts,
                    sceneCost,
                    log =lines.OrderBy(x => x.t).ThenBy(x => x.order).Select(x => x.line).TakeLast(MaxCombinedLogLines).ToArray(),
                    details = r_details.Select(d => new { name = d.Key, content = d.Value.Content.ToString(), truncated = d.Value.Truncated }).ToArray()
                };
            }

            /// <summary>The error lines of the log of this task (called with the gate held).</summary>
            internal string[] GetErrorLines() => r_log.Where(l => l.IsError).Select(l => l.Text).ToArray();

            internal IEnumerable<UiNode> GetSelfAndDescendants() => new[] { this }.Concat(GetDescendants());

            private IEnumerable<UiNode> GetDescendants()
            {
                foreach (var child in r_children)
                {
                    yield return child;
                    foreach (var descendant in child.GetDescendants())
                        yield return descendant;
                }
            }

            private string GetPathFrom(UiNode ancestor) => Parent == null || Parent == ancestor ? Name : $"{Parent.GetPathFrom(ancestor)} > {Name}";

            /// <summary>The start / end of this task, for the combined log of a parent (only the important tasks, and what went wrong).</summary>
            private IEnumerable<(long t, string text, string kind)> GetEvents()
            {
                var important = rs_kindsWithEvents.Contains(Kind);
                if (important && StartedAt != null && State != TaskState.Skipped && State != TaskState.Blocked)
                {
                    yield return (ToMs(StartedAt).Value, $"Started: {Name}", "start");
                }
                if (EndedAt == null)
                    yield break;

                var duration = StartedAt != null ? $" ({EndedAt.Value - StartedAt.Value:hh\\:mm\\:ss})" : string.Empty;
                var reason = string.IsNullOrEmpty(Reason) ? string.Empty : $": {Reason}";
                switch (State)
                {
                    case TaskState.Failed: yield return (ToMs(EndedAt).Value, $"Failed: {Name}{reason}", "fail"); break;
                    case TaskState.Cancelled: yield return (ToMs(EndedAt).Value, $"Stopped: {Name}{reason}", "stop"); break;
                    case TaskState.Blocked: yield return (ToMs(EndedAt).Value, $"Blocked: {Name}{reason}", "block"); break;
                    case TaskState.Succeeded when important: yield return (ToMs(EndedAt).Value, $"Finished: {Name}{duration}", "ok"); break;
                    case TaskState.Skipped when important && Kind == "Worker": yield return (ToMs(EndedAt).Value, $"Skipped: {Name}{reason}", "skip"); break;
                }
            }
        }
    }
}
