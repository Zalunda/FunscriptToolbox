using FunscriptToolbox.SubtitlesVerbs.Infra;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FunscriptToolbox.Progress
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

        /// <summary>The priority of the sequence of the current flow (null when not set, ex. console mode: one sequence at a time).</summary>
        public static LockPriority Current => rs_current.Value;

        /// <summary>True when the current flow is work done in advance, in the background (see <see cref="MarkAsBackgroundWork"/>).</summary>
        public static bool IsBackgroundWork => rs_isBackgroundWork.Value;

        /// <summary>The position of the sequence in the list of sequences processed in parallel (lower = first served).</summary>
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

        private LockPriority(int ordinal)
        {
            this.Ordinal = ordinal;
        }

        /// <summary>To call at the start of the thread of a sequence.</summary>
        public static void SetForSequence(int ordinal)
        {
            rs_current.Value = new LockPriority(ordinal);
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
    /// something that can safely run a few at a time, ex. ffmpeg cutting audio clips).
    /// Re-entrant for the same logical flow of execution, so nested helpers can all ask for the lock.
    /// While waiting, the current node is shown as "waiting for lock".
    /// When a slot is free, it is given to the waiter with the best priority (see <see cref="LockPriority"/>), then the first arrived.
    /// </summary>
    public class ResourceLock
    {
        private sealed class Holder
        {
            public string Description;
        }

        private sealed class Waiter
        {
            public string Description;
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
            public (int tier, int ordinal, long arrival) Rank =>
                (IsLowPriority ? 1 : 0,
                 Priority?.Ordinal ?? 0,
                 Arrival);
        }

        private readonly AsyncLocal<Holder> r_ownedByFlow = new AsyncLocal<Holder>();
        private readonly List<Waiter> r_waiting = new List<Waiter>();
        private readonly List<Holder> r_holders = new List<Holder>();
        private readonly object r_stateLock = new object();
        private long m_nextArrival;

        public string Name { get; }
        public TaskState StateWhileHolding { get; }

        /// <summary>How many holders at the same time.</summary>
        public int MaxConcurrent { get; }

        public ResourceLock(string name, TaskState stateWhileHolding = TaskState.Running, int maxConcurrent = 1)
        {
            if (maxConcurrent < 1)
                throw new ArgumentOutOfRangeException(nameof(maxConcurrent), "A lock needs at least one holder at a time.");

            this.Name = name;
            this.StateWhileHolding = stateWhileHolding;
            this.MaxConcurrent = maxConcurrent;
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
        public IDisposable Acquire(string description, bool cancellable = false)
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
            var holder = new Holder { Description = label };
            var waiter = new Waiter
            {
                Description = label,
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
        // How many of each kind of work at the same time. Constants for now (to be moved to a configuration).
        // The more a kind of work weighs on the machine, the fewer at a time.

        /// <summary>Reading the original video with ffmpeg (taking a screenshot, extracting the audio the first time): hard on the disk.</summary>
        public const int VideoSourceConcurrency = 1;

        /// <summary>ffmpeg working on an audio that is already extracted (converting or filtering a whole audio): a lot less demanding.</summary>
        public const int AudioConcurrency = 3;

        /// <summary>Cutting an audio clip from the PCM in memory and writing it in the temp folder (which should be on an SSD): small and many.</summary>
        public const int ClipConcurrency = 8;

        /// <summary>Only one thing that uses the local machine (local whisper, local AI server, ...) at a time.</summary>
        public ResourceLock LocalResource { get; } = new ResourceLock("local resource (GPU)");

        /// <summary>ffmpeg reading the original video (see <see cref="VideoSourceConcurrency"/>).</summary>
        public ResourceLock VideoSource { get; } = new ResourceLock("video source (ffmpeg on the video)", maxConcurrent: VideoSourceConcurrency);

        /// <summary>ffmpeg on a whole audio (see <see cref="AudioConcurrency"/>).</summary>
        public ResourceLock Audio { get; } = new ResourceLock("audio (ffmpeg on audio)", maxConcurrent: AudioConcurrency);

        /// <summary>Audio clips cut from the PCM in memory (see <see cref="ClipConcurrency"/>).</summary>
        public ResourceLock Clips { get; } = new ResourceLock("audio clips (temp folder)", maxConcurrent: ClipConcurrency);

        /// <summary>Only one prompt/dialog shown to the user at a time.</summary>
        public ResourceLock UserInteraction { get; } = new ResourceLock("user interaction", TaskState.WaitingForUser);

        /// <summary>
        /// Only one sequence prepares its AI request(s) at a time (gathering the screenshots / clips of all its nodes).
        /// Only the main work asks for it (never the background work): see AIRequestGenerator.CreateNextRequests.
        /// </summary>
        public ResourceLock RequestPreparation { get; } = new ResourceLock("AI request preparation");

        public IEnumerable<ResourceLock> All => new[] { LocalResource, VideoSource, Audio, Clips, UserInteraction, RequestPreparation };
    }
}
