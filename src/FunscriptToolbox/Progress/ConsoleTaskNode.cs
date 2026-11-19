using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.Progress
{
    /// <summary>
    /// A task of the console mode. Only keeps what the console progress line needs (state, status, counter):
    /// the logs and details are not kept, and the finished tasks are forgotten.
    /// All the state is protected by the lock of the <see cref="ConsoleUserOutput"/>.
    /// </summary>
    internal sealed class ConsoleTaskNode : ITaskNode
    {
        private readonly ConsoleUserOutput r_owner;
        private readonly List<ConsoleTaskNode> r_children = new List<ConsoleTaskNode>();
        private ITaskNode m_previousCurrent;
        private bool m_isCurrent;

        public ConsoleTaskNode Parent { get; }
        public string Kind { get; }
        public string Name { get; }
        public bool IsBackground { get; }
        public TaskState State { get; private set; } = TaskState.Pending;
        public string Status { get; private set; }
        public long ProgressDone { get; private set; }
        public long? ProgressTotal { get; private set; }
        public string ProgressUnit { get; private set; }

        public ConsoleTaskNode(ConsoleUserOutput owner, ConsoleTaskNode parent, string kind, string name, bool isBackground)
        {
            r_owner = owner;
            Parent = parent;
            Kind = kind;
            Name = name;
            IsBackground = isBackground;
            lock (owner.Gate)
            {
                parent?.r_children.Add(this);
            }
        }

        internal bool IsActive => State.IsActive();

        private bool IsTerminal => State.IsTerminal();

        public ITaskNode AddPendingChild(string kind, string name, bool isBackground = false)
            => new ConsoleTaskNode(r_owner, this, kind, name, isBackground);

        public void Start()
        {
            lock (r_owner.Gate)
            {
                if (State == TaskState.Pending) State = TaskState.Running;

                // The parent is now doing this sub-task: its last status is not what is happening anymore
                // (otherwise it would come back on the progress line when the sub-task is finished)
                if (Parent != null && !IsBackground) Parent.Status = null;
            }
            m_previousCurrent = TaskScope.Current;
            m_isCurrent = true;
            TaskScope.Current = this;
            r_owner.RequestRender();
        }

        public void SetState(TaskState state, string reason = null)
        {
            lock (r_owner.Gate)
            {
                if (IsTerminal) return;
                if (state.IsTerminal())
                {
                    Finish(state);
                }
                else
                {
                    State = state;
                }
            }
            r_owner.RequestRender();
        }

        public void SetStatus(string text)
        {
            lock (r_owner.Gate)
            {
                if (Status == text) return;
                Status = text;
            }
            r_owner.RequestRender();
        }

        public void SetProgress(long done, long? total, string unit = null)
        {
            lock (r_owner.Gate)
            {
                ProgressDone = done;
                ProgressTotal = total;
                if (unit != null) ProgressUnit = unit;
            }
            r_owner.RequestRender();
        }

        public void IncrementProgress(long by = 1)
        {
            lock (r_owner.Gate)
            {
                ProgressDone += by;
            }
            r_owner.RequestRender();
        }

        // The console only shows the progress line: the logs and the details are written by the code itself (WriteInfo, WriteStream...).
        public void AddLogLine(string text, ConsoleColor? color = null, bool isError = false) { }
        public void SetDetail(string name, string content) { }
        public void AppendDetail(string name, string content) { }

        // In the console, the user stops with the Q key (PollStopRequest), not with a button on a task.
        public void SetCancellable(string hint) { }
        public bool ConsumeCancelRequest() => false;

        public void Fail(string message) => SetState(TaskState.Failed, message);

        // (only used by the web UI)
        public void MarkAsRefreshOnly() { }
        public int? Id => null;
        public void SetFlowGraph(object graph) { }
        public void SetFlowInspector(Func<string, object> inspector) { }
        public void SetFlowCounter(Func<object> counter) { }

        private void Finish(TaskState finalState)
        {
            // Called with the gate held.
            State = finalState;
            foreach (var child in r_children.Where(c => c.State == TaskState.Pending).ToArray())
            {
                // What was planned but never started
                child.State = TaskState.Skipped;
            }
            // Finished: only the active tasks are needed for the progress line
            r_children.RemoveAll(c => c.IsTerminal);
            Parent?.r_children.Remove(this);
            r_owner.ForgetRootInternal(this);
        }

        public void Dispose()
        {
            lock (r_owner.Gate)
            {
                if (State != TaskState.Pending && !IsTerminal)
                {
                    Finish(TaskState.Succeeded);
                }
            }
            if (m_isCurrent)
            {
                m_isCurrent = false;
                TaskScope.Current = m_previousCurrent;
            }
            r_owner.RequestRender();
        }

        // ---- Used to compose the progress line (called with the gate held) ----

        /// <summary>The task the main work is doing right now: follows the active sub-tasks that are not in the background.</summary>
        internal ConsoleTaskNode GetForegroundLeaf()
        {
            var current = this;
            while (true)
            {
                var next = current.r_children.FirstOrDefault(c => c.IsActive && !c.IsBackground);
                if (next == null) return current;
                current = next;
            }
        }

        /// <summary>The active background tasks under this task (any depth).</summary>
        internal IEnumerable<ConsoleTaskNode> GetActiveBackgroundTasks()
        {
            foreach (var child in r_children)
            {
                if (child.IsBackground && child.IsActive)
                    yield return child;
                foreach (var descendant in child.GetActiveBackgroundTasks())
                    yield return descendant;
            }
        }

        /// <summary>True when this task, or one of its active sub-tasks, is waiting for the user (a prompt is on the console).</summary>
        internal bool IsWaitingForUser()
        {
            return State == TaskState.WaitingForUser
                || r_children.Any(c => c.IsActive && c.IsWaitingForUser());
        }

        /// <summary>Ex. "12/40 nodes", "42%", or null when there is no counter.</summary>
        internal string GetCountText()
        {
            if (ProgressTotal == null && ProgressDone == 0)
                return null;
            if (ProgressUnit == "%" && ProgressTotal > 0)
                return $"{100.0 * ProgressDone / ProgressTotal.Value:0}%";
            var total = ProgressTotal != null ? $"/{ProgressTotal}" : string.Empty;
            var unit = ProgressUnit != null ? $" {ProgressUnit}" : string.Empty;
            return $"{ProgressDone}{total}{unit}";
        }
    }
}
