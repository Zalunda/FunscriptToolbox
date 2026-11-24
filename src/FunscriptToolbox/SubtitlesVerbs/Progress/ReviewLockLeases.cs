using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FunscriptToolbox.SubtitlesVerbs.Progress
{
    /// <summary>
    /// The locks 'VID' (ffmpeg reading the original videos) and 'ENC' (ffmpeg encoding a video) held by the Review editors open in a browser:
    /// while the user reviews (the video plays in the browser), no ffmpeg reads a video or encodes one in the background, the machine stays responsive.
    /// A page holds its lease by calling <see cref="Hold"/> regularly (see ui.html): without a call for <see cref="LeaseTimeout"/>
    /// (ex. the tab was closed without saying it), its locks are released.
    /// Each lock is taken on a thread of its own (<see cref="ResourceLock.Acquire"/> waits, and is re-entrant by flow): one used for a long time
    /// by someone else (ex. an encoding) doesn't keep the other one from being taken.
    /// </summary>
    internal sealed class ReviewLockLeases : IDisposable
    {
        public static readonly TimeSpan LeaseTimeout = TimeSpan.FromSeconds(30);

        private readonly ResourceLock[] r_locks;
        private readonly object r_gate = new object();
        private readonly Dictionary<string, Lease> r_leases = new Dictionary<string, Lease>();
        private readonly Timer r_watchdog;

        public ReviewLockLeases(SharedLocks locks)
        {
            r_locks = new[] { locks.VideoSource, locks.VideoEncoding };
            r_watchdog = new Timer(_ => ReleaseExpired(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// The page 'clientId' (one by opening of the editor) holds the locks: taken the first time (in the background: they can be used by
        /// someone else for a moment), then kept while it calls again. Returns the state of each lock: held, or who uses it (the page shows it).
        /// </summary>
        public object Hold(string clientId, string sceneName)
        {
            if (string.IsNullOrEmpty(clientId) || clientId.Length > 64)
                throw new TimingEditorException("Invalid or missing 'client'.");

            Lease lease;
            lock (r_gate)
            {
                if (!r_leases.TryGetValue(clientId, out lease))
                {
                    var name = string.IsNullOrWhiteSpace(sceneName) ? "?" : sceneName.Length > 120 ? sceneName.Substring(0, 120) + "..." : sceneName;
                    lease = new Lease(r_locks, $"Review editor: {name}");
                    r_leases[clientId] = lease;
                }
                lease.LastSeen = DateTime.UtcNow;
            }
            return new
            {
                locks = lease.Holds.Select(h => new
                {
                    shortName = h.Lock.ShortName,
                    name = h.Lock.Name,
                    held = h.IsHeld,
                    usedBy = h.IsHeld ? null : h.Lock.CurrentHolderDescription
                }).ToArray()
            };
        }

        /// <summary>The page is closed: its locks are released right away.</summary>
        public void Release(string clientId)
        {
            Lease lease;
            lock (r_gate)
            {
                if (clientId == null || !r_leases.TryGetValue(clientId, out lease))
                    return;
                r_leases.Remove(clientId);
            }
            lease.Dispose();
        }

        /// <summary>True when a Review editor holds the lock right now.</summary>
        public bool IsHeldByAReview(ResourceLock resourceLock)
        {
            lock (r_gate)
            {
                return r_leases.Values.Any(lease => lease.Holds.Any(h => h.Lock == resourceLock && h.IsHeld));
            }
        }

        private void ReleaseExpired()
        {
            List<Lease> expired;
            lock (r_gate)
            {
                var now = DateTime.UtcNow;
                var keys = r_leases.Where(kvp => now - kvp.Value.LastSeen > LeaseTimeout).Select(kvp => kvp.Key).ToArray();
                expired = keys.Select(k => r_leases[k]).ToList();
                foreach (var key in keys)
                    r_leases.Remove(key);
            }
            foreach (var lease in expired)
                lease.Dispose();
        }

        public void Dispose()
        {
            r_watchdog.Dispose();
            List<Lease> all;
            lock (r_gate)
            {
                all = r_leases.Values.ToList();
                r_leases.Clear();
            }
            foreach (var lease in all)
                lease.Dispose();
        }

        private sealed class Lease : IDisposable
        {
            private readonly CancellationTokenSource r_cancel = new CancellationTokenSource();

            public LockHold[] Holds { get; }

            /// <summary>The last call of the page (see Hold). Read / written under the gate of ReviewLockLeases.</summary>
            public DateTime LastSeen;

            public Lease(ResourceLock[] locks, string description)
            {
                this.Holds = locks.Select(l => new LockHold(l, description, r_cancel.Token)).ToArray();
            }

            public void Dispose()
            {
                // (the threads stop waiting / release their lock)
                r_cancel.Cancel();
            }
        }

        /// <summary>One lock of a lease: taken by its own thread (it waits when someone else uses it), kept until the lease ends.</summary>
        private sealed class LockHold
        {
            private int m_isHeld;

            public ResourceLock Lock { get; }

            public bool IsHeld => Volatile.Read(ref m_isHeld) == 1;

            public LockHold(ResourceLock resourceLock, string description, CancellationToken stop)
            {
                this.Lock = resourceLock;
                var thread = new Thread(() =>
                {
                    try
                    {
                        // (no task node: the scenes waiting for it show this description, "used by: Review editor: ...")
                        TaskScope.Current = NullTaskNode.Instance;
                        using (resourceLock.Acquire(description, cancellationToken: stop))
                        {
                            Volatile.Write(ref m_isHeld, 1);
                            stop.WaitHandle.WaitOne();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // (the lease ended while waiting for the lock)
                    }
                    finally
                    {
                        Volatile.Write(ref m_isHeld, 0);
                    }
                })
                { IsBackground = true, Name = $"Review editor lock {resourceLock.ShortName}" };
                thread.Start();
            }
        }
    }
}
