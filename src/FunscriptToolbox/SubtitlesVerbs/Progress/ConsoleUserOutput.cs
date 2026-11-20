using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FunscriptToolbox.SubtitlesVerbs.Progress
{
    /// <summary>
    /// The historical behavior: everything goes to the console.
    /// The tasks (<see cref="ConsoleTaskNode"/>) are combined in a single progress line: what the main work is doing first,
    /// then a summary of what runs in the background (ex. "    Prepare request #3: 12/40 nodes | bg: Audio clips 85/120").
    /// Outside of a task (other verbs, final report...), a progress message is written as is, like before.
    /// </summary>
    public class ConsoleUserOutput : IUserOutput
    {
        /// <summary>The progress line is not redrawn more often than this.</summary>
        private static readonly TimeSpan rs_minRenderInterval = TimeSpan.FromMilliseconds(125);

        /// <summary>After streamed text (ex. tokens of an AI response), the progress line waits this long before coming back.</summary>
        private static readonly TimeSpan rs_streamQuietDelay = TimeSpan.FromMilliseconds(1500);

        private readonly object r_lock = new object();
        private readonly List<ConsoleTaskNode> r_roots = new List<ConsoleTaskNode>();
        private bool m_lastWriteIsProgress = false;
        private string m_lastProgressLine;
        private bool m_renderPending;
        private DateTime m_lastRender = DateTime.MinValue;
        private bool m_streamMidLine;
        private DateTime m_lastStreamWrite = DateTime.MinValue;
        private Timer m_renderTimer;

        internal object Gate => r_lock;

        public bool IsWebUi => false;

        public ITaskNode CreateRootNode(string kind, string name, RootNodeInfo info = null)
        {
            // (the console shows the full name: the short name is for the web UI)
            lock (r_lock)
            {
                var node = new ConsoleTaskNode(this, null, kind, name, isBackground: false);
                r_roots.Add(node);
                // Draws what was throttled or delayed (stream), even when nothing else is written
                m_renderTimer ??= new Timer(_ => OnRenderTimer(), null, rs_minRenderInterval, rs_minRenderInterval);
                return node;
            }
        }

        internal void ForgetRootInternal(ConsoleTaskNode node)
        {
            // Called with the gate held.
            r_roots.Remove(node);
        }

        public void WriteInfo(string message, ConsoleColor? color, bool isProgress)
        {
            if (isProgress && TaskScope.Current is ConsoleTaskNode node)
            {
                node.SetStatus(string.IsNullOrWhiteSpace(message) ? null : message.TrimEnd());
                return;
            }

            lock (r_lock)
            {
                if (m_lastWriteIsProgress)
                {
                    ClearProgressLine();
                }

                if (color != null) Console.ForegroundColor = color.Value;
                if (isProgress)
                {
                    WriteProgressText(message);
                }
                else
                {
                    Console.WriteLine($"{message}");
                    CloseStream();
                }
                Console.ResetColor();
                m_lastWriteIsProgress = isProgress;
                m_lastProgressLine = isProgress ? message : null;
                if (!isProgress) m_renderPending = true; // the progress line comes back under the new line
            }
        }

        public void WriteVerbose(string message, ConsoleColor? color, bool isProgress, bool isVerboseEnabled)
        {
            if (isProgress && isVerboseEnabled && TaskScope.Current is ConsoleTaskNode node)
            {
                node.SetStatus(string.IsNullOrWhiteSpace(message) ? null : message.TrimEnd());
                return;
            }

            lock (r_lock)
            {
                if (m_lastWriteIsProgress)
                {
                    ClearProgressLine();
                }

                if (isVerboseEnabled)
                {
                    if (color != null) Console.ForegroundColor = color.Value;
                    if (isProgress)
                    {
                        Console.Write($"{message}{new string(' ', Math.Max(0, Console.WindowWidth - message.Length - 1))}\r");
                    }
                    else
                    {
                        Console.WriteLine(message);
                        CloseStream();
                    }
                    Console.ResetColor();
                }
                m_lastWriteIsProgress = isProgress;
                m_lastProgressLine = null;
                if (!isProgress) m_renderPending = true;
            }
        }

        public void WriteError(string message)
        {
            lock (r_lock)
            {
                if (m_lastWriteIsProgress)
                {
                    ClearProgressLine();
                    m_lastWriteIsProgress = false;
                    m_lastProgressLine = null;
                }

                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(message);
                Console.ResetColor();
                CloseStream();
                m_renderPending = true;
            }
        }

        public void WriteStream(string text)
        {
            lock (r_lock)
            {
                if (m_lastWriteIsProgress)
                {
                    // The streamed text starts at the beginning of the line: the progress line must not stay under it
                    ClearProgressLine();
                    m_lastWriteIsProgress = false;
                    m_lastProgressLine = null;
                }
                Console.Write(text);
                if (!string.IsNullOrEmpty(text))
                {
                    m_streamMidLine = !text.EndsWith("\n");
                }
                m_lastStreamWrite = DateTime.UtcNow;
            }
        }

        public void WriteStreamLine(string text)
        {
            lock (r_lock)
            {
                if (m_lastWriteIsProgress)
                {
                    ClearProgressLine();
                    m_lastWriteIsProgress = false;
                    m_lastProgressLine = null;
                }
                Console.WriteLine(text);
                m_streamMidLine = false;
                m_lastStreamWrite = DateTime.UtcNow;
                m_renderPending = true;
            }
        }

        public bool PollStopRequest()
        {
            return !Console.IsInputRedirected
                && Console.KeyAvailable
                && Console.ReadKey(intercept: true).Key == ConsoleKey.Q;
        }

        public UserAnswer Ask(UserPrompt prompt)
        {
            throw new NotSupportedException("Prompts are only supported by the web UI.");
        }

        // (the console closes with Ctrl+C: no 'closing' mode, no 'Pause all')
        public string PauseAtSafePointReason => null;
        public string GetPauseAtSafePointReason(int? sceneNodeId) => null;
        public IDisposable EnterPauseForClose() => null;

        // ---- The progress line of the tasks ----

        /// <summary>Called when a task changes: the line is redrawn now, or a little later (throttled, or after a stream).</summary>
        internal void RequestRender()
        {
            lock (r_lock)
            {
                m_renderPending = true;
                if (DateTime.UtcNow - m_lastRender >= rs_minRenderInterval)
                {
                    RenderIfPossible();
                }
            }
        }

        private void OnRenderTimer()
        {
            lock (r_lock)
            {
                if (m_renderPending)
                {
                    RenderIfPossible();
                }
            }
        }

        private void RenderIfPossible()
        {
            // Called with the gate held, sometimes from code that must not fail (ex. releasing a ResourceLock, the timer thread):
            // a problem with the console only turns the progress line off.
            if (m_renderingDisabled)
                return;
            try
            {
                RenderIfPossibleInternal();
            }
            catch (Exception)
            {
                m_renderingDisabled = true;
                m_renderPending = false;
            }
        }

        private bool m_renderingDisabled;

        private void RenderIfPossibleInternal()
        {
            if (r_roots.Count == 0)
            {
                // No task (console mode not started yet, or finished): the progress lines are written as they come
                m_renderPending = false;
                return;
            }

            if (m_streamMidLine || DateTime.UtcNow - m_lastStreamWrite < rs_streamQuietDelay)
            {
                return; // Stays pending: drawn when the stream is quiet (by the timer)
            }

            if (r_roots.Any(r => r.IsWaitingForUser()))
            {
                return; // Stays pending: a prompt is on the console (the line would be drawn over what the user types)
            }

            m_renderPending = false;
            m_lastRender = DateTime.UtcNow;

            var line = ComposeProgressLine();
            if (line == null)
            {
                if (m_lastWriteIsProgress)
                {
                    ClearProgressLine();
                    m_lastWriteIsProgress = false;
                    m_lastProgressLine = null;
                }
                return;
            }

            if (m_lastWriteIsProgress && line == m_lastProgressLine)
                return;

            WriteProgressText(line);
            m_lastWriteIsProgress = true;
            m_lastProgressLine = line;
        }

        /// <returns>null when there is nothing to show.</returns>
        private string ComposeProgressLine()
        {
            // Called with the gate held.
            var mainParts = new List<string>();
            var backgroundParts = new List<string>();
            foreach (var root in r_roots.Where(r => r.IsActive))
            {
                var leaf = root.GetForegroundLeaf();
                var count = leaf.GetCountText();
                var status = leaf.Status?.Trim();
                if (count != null)
                {
                    mainParts.Add(status != null ? $"{leaf.Name}: {count} - {status}" : $"{leaf.Name}: {count}");
                }
                else if (status != null)
                {
                    mainParts.Add(status);
                }

                foreach (var background in root.GetActiveBackgroundTasks())
                {
                    var backgroundCount = background.GetCountText() ?? background.Status?.Trim();
                    backgroundParts.Add(backgroundCount != null ? $"{background.Name} {backgroundCount}" : background.Name);
                }
            }

            if (mainParts.Count == 0 && backgroundParts.Count == 0)
                return null;

            var line = "    " + string.Join(" / ", mainParts);
            if (backgroundParts.Count > 0)
            {
                // At the end: when the line is too long, the background is what is cut
                line += (mainParts.Count > 0 ? " | " : string.Empty) + "bg: " + string.Join(", ", backgroundParts);
            }
            return line;
        }

        private static void WriteProgressText(string message)
        {
            var width = Console.WindowWidth;
            var padding = width - message.Length - 1;
            Console.Write(
                (padding < 0)
                ? $"{message.Substring(0, width - 1)}\r"
                : $"{message}{new string(' ', padding)}\r");
        }

        private static void ClearProgressLine()
        {
            Console.Write($"{new string(' ', Math.Max(0, Console.WindowWidth - 1))}\r");
        }

        private void CloseStream()
        {
            // A line written by the code ends what was streamed on the current line
            m_streamMidLine = false;
        }
    }
}
