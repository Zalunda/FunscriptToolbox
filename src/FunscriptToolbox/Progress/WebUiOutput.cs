using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FunscriptToolbox.Progress
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

        public bool IsWebUi => true;

        internal long Version => Interlocked.Read(ref m_version);
        internal void Touch() => Interlocked.Increment(ref m_version);

        public void MarkFinished()
        {
            lock (this.Gate)
            {
                this.IsFinished = true;
                Touch();
            }
        }

        public ITaskNode CreateRootNode(string kind, string name)
        {
            lock (this.Gate)
            {
                var node = new UiNode(this, null, kind, name);
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
            else
            {
                CurrentNode.AddLogLine(message, color);
            }
        }

        public void WriteVerbose(string message, ConsoleColor? color, bool isProgress, bool isVerboseEnabled)
        {
            if (isProgress)
            {
                CurrentNode.SetStatus(message?.Trim());
            }
            else if (isVerboseEnabled)
            {
                CurrentNode.AddLogLine(message, color);
            }
        }

        public void WriteError(string message)
        {
            CurrentNode.AddLogLine(message, ConsoleColor.Red, isError: true);
            // Also on the console, so errors are not lost if the page is not open.
            lock (this.Gate)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(message);
                Console.ResetColor();
            }
        }

        public void WriteStream(string text)
        {
            if (!string.IsNullOrEmpty(text)) CurrentNode.AppendDetail("Live output", text);
        }

        public void WriteStreamLine(string text)
        {
            CurrentNode.AppendDetail("Live output", (text ?? string.Empty) + "\n");
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

        internal bool RequestCancel(int nodeId)
        {
            lock (this.Gate)
            {
                if (!m_nodesById.TryGetValue(nodeId, out var node)) return false;
                node.RequestCancelInternal();
                return true;
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
                    locks = this.Locks.All.Select(l => new
                    {
                        name = l.Name,
                        holder = l.CurrentHolderDescription,
                        waiting = l.WaitingDescriptions
                    }).ToArray()
                };
            }
        }

        internal object CreateNodeDetail(int nodeId)
        {
            lock (this.Gate)
            {
                if (!m_nodesById.TryGetValue(nodeId, out var node)) return null;
                return node.ToDetail();
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
        }

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
            public int ErrorCount { get; private set; }
            public bool IsBackground { get; }
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

            private bool IsTerminal => State == TaskState.Succeeded || State == TaskState.Failed || State == TaskState.Cancelled || State == TaskState.Skipped;

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
                    if (state == TaskState.Succeeded || state == TaskState.Skipped || state == TaskState.Failed || state == TaskState.Cancelled)
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

            public void AddLogLine(string text, ConsoleColor? color = null, bool isError = false)
            {
                lock (r_owner.Gate)
                {
                    r_log.Add(new LogLine
                    {
                        TimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Text = text ?? string.Empty,
                        Color = color?.ToString(),
                        IsError = isError
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
                CancelRequests++;
                AddLogLine($"Stop requested by the user (request #{CancelRequests}).");
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
                    cancelRequests = CancelRequests - m_cancelConsumed,
                    background = IsBackground,
                    progressDone = (m_progressTotal != null || m_progressDone > 0) ? m_progressDone : (long?)null,
                    progressTotal = m_progressTotal,
                    progressUnit = m_progressUnit
                };
            }

            public object ToDetail()
            {
                return new
                {
                    id = Id,
                    path = GetPath(),
                    log = r_log.Select(l => new { t = l.TimeMs, text = l.Text, color = l.Color, error = l.IsError }).ToArray(),
                    details = r_details.Select(d => new { name = d.Key, content = d.Value.Content.ToString(), truncated = d.Value.Truncated }).ToArray()
                };
            }
        }
    }
}
