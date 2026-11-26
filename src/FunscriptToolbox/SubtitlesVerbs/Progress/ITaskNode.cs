using System;
using System.Threading;

namespace FunscriptToolbox.SubtitlesVerbs.Progress
{
    public enum TaskState
    {
        Pending,
        WaitingForLock,
        WaitingForUser,
        Running,
        Succeeded,
        Skipped,
        Failed,
        Cancelled,
        /// <summary>Could not be done yet: something it needs is missing (ex. a worker whose prerequisites are not met).</summary>
        Blocked
    }

    public static class TaskStateExtensions
    {
        /// <summary>A state that ends the task (it can't change anymore).</summary>
        public static bool IsTerminal(this TaskState state)
        {
            return state == TaskState.Succeeded
                || state == TaskState.Skipped
                || state == TaskState.Failed
                || state == TaskState.Cancelled
                || state == TaskState.Blocked;
        }

        /// <summary>A task that is started and not finished.</summary>
        public static bool IsActive(this TaskState state)
        {
            return state == TaskState.Running
                || state == TaskState.WaitingForLock
                || state == TaskState.WaitingForUser;
        }
    }

    /// <summary>
    /// One item of the progress tree (sequence => worker => AI request, etc.).
    /// Outside of any task (no current node), the current node is the <see cref="NullTaskNode"/> (does nothing).
    /// Disposing a started node completes it (as Succeeded, unless it was already marked otherwise)
    /// and restores the previous "current" node of the thread.
    /// </summary>
    public interface ITaskNode : IDisposable
    {
        /// <summary>True for a task that runs in the background, beside the main work (ex. audio clips generated in advance).</summary>
        bool IsBackground { get; }

        /// <summary>Creates a child that is not started yet (state Pending). Doesn't change the current node.</summary>
        /// <param name="isBackground">True for a task that will run in the background (it must be started and disposed by the background code itself).</param>
        ITaskNode AddPendingChild(string kind, string name, bool isBackground = false);

        /// <summary>Starts a node created with <see cref="AddPendingChild"/>: it becomes Running and the current node.</summary>
        void Start();

        void SetState(TaskState state, string reason = null);

        /// <summary>The short "what is happening right now" text (replaces the console 'progress' line).</summary>
        void SetStatus(string text);

        /// <summary>
        /// Counter for work done item by item (ex. 12/40 nodes, 85/120 clips). The items are never nodes themselves.
        /// </summary>
        /// <param name="total">null when the total is not known.</param>
        /// <param name="unit">What is counted (ex. "nodes", "clips", "%").</param>
        void SetProgress(long done, long? total, string unit = null);

        /// <summary>Adds to the counter of <see cref="SetProgress"/> (thread-safe, for work done in parallel).</summary>
        void IncrementProgress(long by = 1);

        void AddLogLine(string text, ConsoleColor? color = null, bool isError = false);

        /// <summary>Replaces the content of a named detail (ex. the request sent to an AI).</summary>
        void SetDetail(string name, string content);

        /// <summary>Appends to a named detail (ex. the tokens of an AI response as they are received).</summary>
        void AppendDetail(string name, string content);

        /// <summary>
        /// Tells the UI that the user can ask this node to stop right away ('Stop now': the code must call <see cref="ConsumeCancelRequest"/>).
        /// Null: not stoppable anymore.
        /// </summary>
        void SetCancellable(string hint);

        /// <summary>
        /// An AI request: the UI also offers 'Pause after this request'. It's 'Pause scene': the scene pauses at its next safe point,
        /// once the response of the request is saved (the code calls <c>PauseIfClosing</c> between two requests), until 'Resume scene'.
        /// </summary>
        void SetPauseAfterSupported();

        /// <summary>A 'Stop now' changed by the code into a stop after this request (see <see cref="ConvertToStopAfter"/>): the worker stops once the response is saved.</summary>
        bool IsStopAfterRequested { get; }

        /// <summary>
        /// Besides 'Stop' (the worker stops, it continues at the next run), the UI offers a button to skip the rest of what this node
        /// does, as a choice of the user (ex. 'No more title cards': the work continues as if there was nothing more to find).
        /// The code reads <see cref="IsSkipRequested"/>. Null label: not offered anymore.
        /// </summary>
        /// <param name="label">The text of the button.</param>
        /// <param name="hint">Its tooltip, also the confirmation asked before it's done.</param>
        void SetSkippable(string label, string hint);

        /// <summary>The user clicked on the button of <see cref="SetSkippable"/>.</summary>
        bool IsSkipRequested { get; }

        /// <summary>The code turns a 'Stop now' into a stop after this request (ex. the batch finished before it could be cancelled: its response is kept, then the worker stops).</summary>
        void ConvertToStopAfter(string reason);

        /// <summary>
        /// The warning shown (to confirm) when the user clicks 'Stop now' (ex. what was already sent to the AI is billed), or null:
        /// no confirmation (ex. a batch still queued: cancelled for free).
        /// </summary>
        void SetStopNowWarning(string warning);

        /// <summary>True once for each 'Stop now' request made by the user on this node or one of its ancestors.</summary>
        bool ConsumeCancelRequest();

        void Fail(string message);

        /// <summary>
        /// The task only refreshes what was already done (ex. a worker with 'CanBeUpdated' that runs again):
        /// in the UI, a scene where nothing else was done is 'untouched'.
        /// </summary>
        void MarkAsRefreshOnly();

        /// <summary>The id of the node in the web UI (null for the NullTaskNode).</summary>
        int? Id { get; }

        /// <summary>The graph of the workers and data of a scene (only on a first-level node: the 'Flow' view of the web UI).</summary>
        void SetFlowGraph(object graph);

        /// <summary>How to read the items of a box of the graph (the inspector of the 'Flow' view), given the key of the box.</summary>
        void SetFlowInspector(Func<string, object> inspector);

        /// <summary>How to count the items of the boxes of the graph (shown in a corner of the boxes of the 'Flow' view).</summary>
        void SetFlowCounter(Func<object> counter);

        /// <summary>How to read the total cost of the AI of the scene, in $ (estimate; null: no AI cost yet): a pill beside its name (only on a first-level node).</summary>
        void SetCostProvider(Func<double?> provider);

        /// <summary>
        /// How to read the screenshot sent to the AI for an item (key of the worker, name of the extractor, start, end):
        /// shown when asked in the tables of the inspector. Null: no image.
        /// </summary>
        void SetFlowImageProvider(Func<string, string, TimeSpan, TimeSpan, bool, byte[]> provider);

        /// <summary>The data of the 'Timing' editor of the web UI for this scene (only on a first-level node).</summary>
        void SetTimingEditor(ITimingEditor editor);

        /// <summary>The configuration of the steps of this scene (the 'Config' tab of the inspector of the 'Flow' view; only on a first-level node).</summary>
        void SetConfigView(ISceneConfigView view);

        /// <summary>
        /// The task waits for something the user does in the web UI (ex. "timing:&lt;review id&gt;": a review in the Timing editor):
        /// its scene is shown as 'needs you', with a button for it.
        /// </summary>
        void SetUserAction(string action);
    }

    public sealed class NullTaskNode : ITaskNode
    {
        public static readonly NullTaskNode Instance = new NullTaskNode();
        private NullTaskNode() { }

        public bool IsBackground => false;
        public ITaskNode AddPendingChild(string kind, string name, bool isBackground = false) => this;
        public void Start() { }
        public void SetState(TaskState state, string reason = null) { }
        public void SetStatus(string text) { }
        public void SetProgress(long done, long? total, string unit = null) { }
        public void IncrementProgress(long by = 1) { }
        public void AddLogLine(string text, ConsoleColor? color = null, bool isError = false) { }
        public void SetDetail(string name, string content) { }
        public void AppendDetail(string name, string content) { }
        public void SetCancellable(string hint) { }
        public void SetPauseAfterSupported() { }
        public bool IsStopAfterRequested => false;
        public void SetSkippable(string label, string hint) { }
        public bool IsSkipRequested => false;
        public void ConvertToStopAfter(string reason) { }
        public void SetStopNowWarning(string warning) { }
        public bool ConsumeCancelRequest() => false;
        public void Fail(string message) { }
        public void MarkAsRefreshOnly() { }
        public int? Id => null;
        public void SetFlowGraph(object graph) { }
        public void SetFlowInspector(Func<string, object> inspector) { }
        public void SetFlowCounter(Func<object> counter) { }
        public void SetCostProvider(Func<double?> provider) { }
        public void SetFlowImageProvider(Func<string, string, TimeSpan, TimeSpan, bool, byte[]> provider) { }
        public void SetTimingEditor(ITimingEditor editor) { }
        public void SetConfigView(ISceneConfigView view) { }
        public void SetUserAction(string action) { }
        public void Dispose() { }
    }

    /// <summary>
    /// The node the current thread (and the tasks/threads started from it) is working for.
    /// </summary>
    public static class TaskScope
    {
        private static readonly AsyncLocal<ITaskNode> rs_current = new AsyncLocal<ITaskNode>();

        public static ITaskNode Current
        {
            get => rs_current.Value ?? NullTaskNode.Instance;
            set => rs_current.Value = value;
        }
    }
}
