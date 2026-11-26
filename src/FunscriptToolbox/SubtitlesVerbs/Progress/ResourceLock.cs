using FunscriptToolbox.SubtitlesVerbs.Infra;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FunscriptToolbox.SubtitlesVerbs.Progress
{
    /// <summary>
    /// Who has priority when several flows are waiting for the same <see cref="ResourceLock"/>.
    /// - The work that a request (or the user) is waiting for goes before the work done in advance, in the background.
    /// - Then, the first sequence (video) goes before the next ones: it 'keeps the key' while it has something to do,
    ///   instead of having the key go from one video to the next one after each screenshot / clip.
    /// Both values flow to the tasks started from the flow (AsyncLocal), so a background task started for a sequence has the ordinal of its sequence.
    /// </summary>
    public sealed class LockPriority
    {
        private static readonly AsyncLocal<LockPriority> rs_current = new AsyncLocal<LockPriority>();
        private static readonly AsyncLocal<bool> rs_isBackgroundWork = new AsyncLocal<bool>();

        /// <summary>The priority of the sequence of the current flow (null when not set, ex. the main thread).</summary>
        public static LockPriority Current => rs_current.Value;

        /// <summary>True when the current flow is work done in advance, in the background (see <see cref="MarkAsBackgroundWork"/>).</summary>
        public static bool IsBackgroundWork => rs_isBackgroundWork.Value;

        /// <summary>The priority level of the sequence (lower = first served, before the ordinal): see SubtitleGeneratorGlobalSharedSettings.ScenePriorities.</summary>
        public int Level { get; }

        /// <summary>The position of the sequence in the list of sequences processed in parallel (lower = first served, at the same level).</summary>
        public int Ordinal { get; }

        private int m_nbForegroundWaitingForBackground;
        private int m_nbPreparations;
        private static int rs_nbPreparations;

        /// <summary>
        /// True while another sequence is preparing a request (see <see cref="BeginPreparation"/>): the background work of this
        /// sequence must not get the ffmpeg locks then, even if they are free for a moment (between two screenshots / clips of the request).
        /// </summary>
        public bool IsAnotherSequencePreparing =>
            Volatile.Read(ref rs_nbPreparations) - Volatile.Read(ref m_nbPreparations) > 0;

        /// <summary>
        /// True while the main work of the sequence is waiting for something that its background work is doing
        /// (ex. the request needs the clip that the background is extracting right now): the background work of the sequence
        /// then has the priority of the main work (otherwise, the request would wait behind the background work of all the other sequences).
        /// </summary>
        public bool IsForegroundWaitingForBackground => Volatile.Read(ref m_nbForegroundWaitingForBackground) > 0;

        private LockPriority(int level, int ordinal)
        {
            this.Level = level;
            this.Ordinal = ordinal;
        }

        /// <summary>To call at the start of the thread of a sequence.</summary>
        public static void SetForSequence(int level, int ordinal)
        {
            rs_current.Value = new LockPriority(level, ordinal);
        }

        /// <summary>To call at the start of a background task (ex. in the Task.Run lambda). Only changes the flow of that task.</summary>
        public static void MarkAsBackgroundWork()
        {
            rs_isBackgroundWork.Value = true;
        }

        /// <summary>
        /// To use (with 'using') when the main work waits for something that its background work might be doing
        /// (see <see cref="IsForegroundWaitingForBackground"/>).
        /// </summary>
        public static IDisposable ForegroundWaitingForBackground()
        {
            var priority = Current;
            if (priority == null || IsBackgroundWork)
                return null; // 'using' accepts null

            Interlocked.Increment(ref priority.m_nbForegroundWaitingForBackground);
            return new ForegroundWaitingReleaser(priority);
        }

        /// <summary>
        /// To use (with 'using') while the main work of the sequence prepares a request (gathers the screenshots / clips of its nodes).
        /// Meanwhile, the background work of the other sequences waits (see <see cref="IsAnotherSequencePreparing"/>).
        /// </summary>
        public static IDisposable BeginPreparation()
        {
            var priority = Current;
            if (priority == null || IsBackgroundWork)
                return null; // 'using' accepts null

            Interlocked.Increment(ref priority.m_nbPreparations);
            Interlocked.Increment(ref rs_nbPreparations);
            return new PreparationReleaser(priority);
        }

        private sealed class PreparationReleaser : IDisposable
        {
            private LockPriority m_priority;

            public PreparationReleaser(LockPriority priority)
            {
                m_priority = priority;
            }

            public void Dispose()
            {
                var priority = Interlocked.Exchange(ref m_priority, null);
                if (priority != null)
                {
                    Interlocked.Decrement(ref rs_nbPreparations);
                    Interlocked.Decrement(ref priority.m_nbPreparations);
                }
            }
        }

        private sealed class ForegroundWaitingReleaser : IDisposable
        {
            private LockPriority m_priority;

            public ForegroundWaitingReleaser(LockPriority priority)
            {
                m_priority = priority;
            }

            public void Dispose()
            {
                var priority = Interlocked.Exchange(ref m_priority, null);
                if (priority != null)
                {
                    Interlocked.Decrement(ref priority.m_nbForegroundWaitingForBackground);
                }
            }
        }
    }

    /// <summary>
    /// Allows only a limited number of holders at a time (one by default: ex. only one prompt shown to the user; several for
    /// something that can safely run a few at a time, ex. ffmpeg converting a whole audio).
    /// Re-entrant for the same logical flow of execution, so nested helpers can all ask for the lock.
    /// While waiting, the current node is shown as "waiting for lock".
    /// When a slot is free, it is given to the waiter with the best priority (see <see cref="LockPriority"/>), then the first arrived.
    /// </summary>
    public class ResourceLock
    {
        private sealed class Holder
        {
            public string Description;
            /// <summary>The task that took the lock (shown in the UI).</summary>
            public ITaskNode Node;
        }

        private sealed class Waiter
        {
            public string Description;
            /// <summary>The task that waits for the lock (shown in the UI).</summary>
            public ITaskNode Node;
            public bool IsBackground;
            public LockPriority Priority;
            public long Arrival;

            /// <summary>Background work that the main work of its sequence is not waiting for.</summary>
            public bool IsLowPriority => IsBackground && Priority?.IsForegroundWaitingForBackground != true;

            /// <summary>
            /// A low priority waiter doesn't get the lock at all while another sequence prepares a request, even when the lock
            /// is free: the request takes its screenshots / clips one after the other, and the lock is free for a moment between them.
            /// </summary>
            public bool IsOnHold => IsLowPriority && Priority?.IsAnotherSequencePreparing == true;

            // (computed each time: the priority of a background waiter changes when its main work starts waiting for it)
            public (int tier, int level, int ordinal, long arrival) Rank =>
                (IsLowPriority ? 1 : 0,
                 Priority?.Level ?? 0,
                 Priority?.Ordinal ?? 0,
                 Arrival);
        }

        private readonly AsyncLocal<Holder> r_ownedByFlow = new AsyncLocal<Holder>();
        private readonly List<Waiter> r_waiting = new List<Waiter>();
        private readonly List<Holder> r_holders = new List<Holder>();
        private readonly object r_stateLock = new object();
        private long m_nextArrival;

        public string Name { get; }

        /// <summary>A few letters, shown in the UI (ex. "GPU").</summary>
        public string ShortName { get; }

        /// <summary>What the lock protects, shown as a tooltip in the UI.</summary>
        public string Description { get; }

        public TaskState StateWhileHolding { get; }

        /// <summary>How many holders at the same time.</summary>
        public int MaxConcurrent { get; }

        public ResourceLock(string name, string shortName, string description, TaskState stateWhileHolding = TaskState.Running, int maxConcurrent = 1)
        {
            if (maxConcurrent < 1)
                throw new ArgumentOutOfRangeException(nameof(maxConcurrent), "A lock needs at least one holder at a time.");

            this.Name = name;
            this.ShortName = shortName;
            this.Description = description;
            this.StateWhileHolding = stateWhileHolding;
            this.MaxConcurrent = maxConcurrent;
        }

        /// <summary>Who holds the lock and who waits for it, right now (a copy: the nodes are not called while the lock state is locked).</summary>
        public (IReadOnlyList<(string Description, ITaskNode Node)> Holders, IReadOnlyList<(string Description, ITaskNode Node)> Waiters) GetUsage()
        {
            lock (r_stateLock)
            {
                return (
                    r_holders.Select(h => (h.Description, h.Node)).ToArray(),
                    r_waiting.OrderBy(w => w.Rank).Select(w => (w.Description, w.Node)).ToArray());
            }
        }

        /// <summary>What the current holders are doing (null when nobody holds the lock).</summary>
        public string CurrentHolderDescription
        {
            get
            {
                lock (r_stateLock)
                {
                    return r_holders.Count == 0 ? null : string.Join("; ", r_holders.Select(h => h.Description));
                }
            }
        }

        public string[] WaitingDescriptions
        {
            get
            {
                lock (r_stateLock)
                {
                    return r_waiting.OrderBy(w => w.Rank).Select(w => w.Description).ToArray();
                }
            }
        }

        /// <param name="description">What will be done while holding the lock.</param>
        /// <param name="cancellable">If true, the user can cancel the wait with the node's cancel button (throws <see cref="UserStoppedWorkerException"/>).</param>
        /// <param name="cancellationToken">Ends the wait (throws <see cref="OperationCanceledException"/>), ex. for a background work stopped by the user.</param>
        public IDisposable Acquire(string description, bool cancellable = false, CancellationToken cancellationToken = default)
        {
            var owned = r_ownedByFlow.Value;
            if (owned != null)
            {
                bool stillHeld;
                lock (r_stateLock) { stillHeld = r_holders.Contains(owned); }
                if (stillHeld)
                    return NoopReleaser.Instance; // Already held by this flow.
            }

            var node = TaskScope.Current;
            var label = $"{description}";
            var holder = new Holder { Description = label, Node = node };
            var waiter = new Waiter
            {
                Description = label,
                Node = node,
                IsBackground = LockPriority.IsBackgroundWork,
                Priority = LockPriority.Current
            };

            bool granted;
            lock (r_stateLock)
            {
                waiter.Arrival = m_nextArrival++;
                granted = TryGrant(waiter, holder, isWaiting: false);
                if (!granted)
                {
                    r_waiting.Add(waiter);
                }
            }

            if (!granted)
            {
                // (never call the node while holding r_stateLock: the web UI reads the state of the locks while holding its own lock)
                node.SetState(TaskState.WaitingForLock, $"waiting for '{Name}' (used by: {CurrentHolderDescription ?? "?"})");
                if (cancellable) node.SetCancellable("Cancel the wait");
                try
                {
                    while (true)
                    {
                        lock (r_stateLock)
                        {
                            if (TryGrant(waiter, holder, isWaiting: true))
                                break;
                            Monitor.Wait(r_stateLock, 250);
                            if (TryGrant(waiter, holder, isWaiting: true))
                                break;
                        }

                        if (cancellable && node.ConsumeCancelRequest())
                        {
                            throw new UserStoppedWorkerException();
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                finally
                {
                    lock (r_stateLock)
                    {
                        if (r_waiting.Remove(waiter))
                        {
                            // (still in the list = the wait was cancelled: another waiter might be the best one now)
                            Monitor.PulseAll(r_stateLock);
                        }
                    }
                    if (cancellable) node.SetCancellable(null);
                }
            }

            r_ownedByFlow.Value = holder;
            node.SetState(this.StateWhileHolding, this.StateWhileHolding == TaskState.Running ? null : description);
            return new Releaser(this, holder, node);
        }

        /// <summary>Must be called while holding r_stateLock.</summary>
        private bool TryGrant(Waiter candidate, Holder holder, bool isWaiting)
        {
            var nbFreeSlots = this.MaxConcurrent - r_holders.Count;
            if (nbFreeSlots <= 0)
                return false;

            if (candidate.IsOnHold)
                return false;

            // The free slots go to the best waiters first.
            var candidateRank = candidate.Rank;
            // (the waiters on hold don't count: they can't take the slot anyway)
            var nbBetterWaiters = r_waiting.Count(w => w != candidate && !w.IsOnHold && w.Rank.CompareTo(candidateRank) < 0);
            if (nbBetterWaiters >= nbFreeSlots)
                return false;

            if (isWaiting)
            {
                r_waiting.Remove(candidate);
            }
            r_holders.Add(holder);
            return true;
        }

        private void Release(Holder holder)
        {
            lock (r_stateLock)
            {
                r_holders.Remove(holder);
                Monitor.PulseAll(r_stateLock);
            }
        }

        private sealed class NoopReleaser : IDisposable
        {
            public static readonly NoopReleaser Instance = new NoopReleaser();
            public void Dispose() { }
        }

        private sealed class Releaser : IDisposable
        {
            private readonly ResourceLock r_owner;
            private readonly Holder r_holder;
            private readonly ITaskNode r_node;
            private int m_disposed;

            public Releaser(ResourceLock owner, Holder holder, ITaskNode node)
            {
                r_owner = owner;
                r_holder = holder;
                r_node = node;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref m_disposed, 1) == 0)
                {
                    r_node.SetState(TaskState.Running);
                    r_owner.Release(r_holder);
                }
            }
        }
    }

    /// <summary>The locks shared by all the sequences processed at the same time.</summary>
    public class SharedLocks
    {
        // How many of each kind of work at the same time, by default (can be changed in the configuration: GlobalShared.MaxConcurrent).
        // The more a kind of work weighs on the machine, the fewer at a time.

        /// <summary>Only one thing that uses the local machine (local whisper, local AI server, ...) at a time.</summary>
        public const int DefaultLocalResourceConcurrency = 1;

        /// <summary>Reading the original video with ffmpeg (taking a screenshot, extracting the audio the first time): hard on the disk.</summary>
        public const int DefaultVideoSourceConcurrency = 1;

        /// <summary>ffmpeg working on an audio that is already extracted (converting or filtering a whole audio): a lot less demanding.</summary>
        public const int DefaultAudioConcurrency = 3;

        /// <summary>One sequence at a time prepares its AI request(s).</summary>
        public const int DefaultRequestPreparationConcurrency = 1;

        /// <summary>Creating a whole video (ex. the light '.FAST.mp4' of a part): long, uses all the processor.</summary>
        public const int DefaultVideoEncodingConcurrency = 1;

        /// <param name="maxConcurrent">
        /// How many at the same time, by short name of lock (GPU, VID, AUD, PREP, ENC). A lock not listed keeps its default value.
        /// An unknown name, 'USER', or a value lower than 1 is an error.
        /// </param>
        public SharedLocks(IReadOnlyDictionary<string, int> maxConcurrent = null)
        {
            var configurable = new[] { "GPU", "VID", "AUD", "PREP", "ENC" };
            var limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in maxConcurrent ?? new Dictionary<string, int>())
            {
                if (!configurable.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
                {
                    throw new Exception($"Unknown lock '{kvp.Key}' in 'GlobalShared.MaxConcurrent' (the locks that can be changed: {string.Join(", ", configurable)}; 'USER' is always 1).");
                }
                if (kvp.Value < 1)
                {
                    throw new Exception($"'GlobalShared.MaxConcurrent.{kvp.Key}' must be at least 1 (received: {kvp.Value}).");
                }
                limits[kvp.Key] = kvp.Value;
            }
            int Get(string shortName, int defaultValue) => limits.TryGetValue(shortName, out var value) ? value : defaultValue;
            static string AtATime(int n) => n == 1 ? "One at a time." : $"{n} at a time.";

            var gpu = Get("GPU", DefaultLocalResourceConcurrency);
            this.LocalResource = new ResourceLock(
                "local resource (GPU)", "GPU",
                $"Local machine (GPU): local transcription tool (Purfview whisper), AI engines with 'UseLocalResourceLock'. {AtATime(gpu)}",
                maxConcurrent: gpu);

            var vid = Get("VID", DefaultVideoSourceConcurrency);
            this.VideoSource = new ResourceLock(
                "video source (ffmpeg on the video)", "VID",
                $"ffmpeg reading the original video: screenshots, audio extraction, title card detection. {AtATime(vid)}",
                maxConcurrent: vid);

            var aud = Get("AUD", DefaultAudioConcurrency);
            this.Audio = new ResourceLock(
                "audio (ffmpeg on audio)", "AUD",
                $"ffmpeg on a whole audio already extracted (conversion, filters). {AtATime(aud)}",
                maxConcurrent: aud);

            this.UserInteraction = new ResourceLock(
                "user interaction", "USER",
                "A prompt or a dialog shown to you (JSON correction, pause, speaker correction). One at a time.",
                TaskState.WaitingForUser);

            var prep = Get("PREP", DefaultRequestPreparationConcurrency);
            this.RequestPreparation = new ResourceLock(
                "AI request preparation", "PREP",
                $"Preparing an AI request (gathering the screenshots / audio clips of its nodes). {(prep == 1 ? "One video at a time." : $"{prep} videos at a time.")}",
                maxConcurrent: prep);

            var enc = Get("ENC", DefaultVideoEncodingConcurrency);
            this.VideoEncoding = new ResourceLock(
                "video encoding (ffmpeg)", "ENC",
                $"ffmpeg creating a whole video (ex. the light '.FAST.mp4' of a part, for the Review editor). {AtATime(enc)}",
                maxConcurrent: enc);
        }

        /// <summary>The things that use the local machine (local whisper, local AI server, ...).</summary>
        public ResourceLock LocalResource { get; }

        /// <summary>ffmpeg reading the original video.</summary>
        public ResourceLock VideoSource { get; }

        /// <summary>ffmpeg on a whole audio.</summary>
        public ResourceLock Audio { get; }

        /// <summary>Only one prompt/dialog shown to the user at a time.</summary>
        public ResourceLock UserInteraction { get; }

        /// <summary>
        /// The sequences that prepare their AI request(s) (gathering the screenshots / clips of all their nodes).
        /// Only the main work asks for it (never the background work): see AIRequestGenerator.CreateNextRequests.
        /// </summary>
        public ResourceLock RequestPreparation { get; }

        /// <summary>ffmpeg creating a whole video (long, uses all the processor).</summary>
        public ResourceLock VideoEncoding { get; }

        public IEnumerable<ResourceLock> All => new[] { LocalResource, VideoSource, Audio, UserInteraction, RequestPreparation, VideoEncoding };
    }
}
