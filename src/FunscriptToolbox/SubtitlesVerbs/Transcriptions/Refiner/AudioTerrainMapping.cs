using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>
    /// Cuts the whole audio into "not speech" stretches and "mountains" (pieces of suspected speech).
    ///
    /// 1. TERRAIN      : RMS envelope (pitch teeth removed, lightly smoothed).
    /// 2. NOISE FLOOR  : local lower bound of the terrain (low percentile per block, then the lowest nearby block).
    /// 3. SPEECH ZONES : stretches where the terrain stays above floor * SpeechRatio and reaches floor * PeakRatio.
    ///                   The edges are then extended down the slopes until they reach the floor.
    /// 4. CUTS         : inside a zone, every local minimum is a candidate. A cut is kept only if it "reaches down":
    ///                   (valley - floor) / (lowerPeak - floor) &lt;= ValleyReach.
    ///
    /// The segments are contiguous and cover the whole audio.
    /// </summary>
    public class AudioTerrainMapping
    {
        // ---------------------------------------------------------------------
        // The only knobs
        // ---------------------------------------------------------------------

        /// <summary>Terrain must be above (floor * this) to be suspected speech. Lower = more sensitive.</summary>
        public float SpeechRatio { get; set; } = 3f;

        /// <summary>A suspected-speech zone must reach (floor * this) somewhere, otherwise it is just noise.</summary>
        public float PeakRatio { get; set; } = 6f;

        /// <summary>A valley is a cut if it comes within this fraction of the way from the floor to the lower neighbouring peak. Lower = fewer cuts.</summary>
        public float ValleyReach { get; set; } = 0.25f;


        // Fixed internals (not worth exposing)
        private const int MsPerFrame = 1;
        private const float MaxFilterMs = 6f;          // removes pitch teeth
        private const float SmoothMs = 3f;
        private const float FloorBlockMs = 250f;       // block size for the noise floor
        private const int FloorNeighbourBlocks = 32;    // lowest block within +/- 64 blocks (= 8 s)
        private const float FloorPercentile = 0.10f;
        private const float MinFloorFraction = 0.01f;  // floor never below 1% of the typical loud level
        private const float MaxGapMs = 150f;           // gaps shorter than this do not split a zone
        private const float MinZoneMs = 80f;
        private const float ValleyWindowMs = 15f;      // local-minimum window
        private const float SlopeMaxMs = 150f;         // how far an edge may slide down a slope
        private const float SlopeEndRatio = 1.2f;      // ...or until terrain is within 20% of the floor
        private const float SlopeExtraStartMs = 30f; // extra margin always added to the start slide
        private const float SlopeExtraEndMs = 30f;    // extra margin always added to the end slide
        private const float SilenceTailSplitMs = 100f; // split long silence after a mountain at +100ms

        // ---------------------------------------------------------------------
        // Result type
        // ---------------------------------------------------------------------

        public class SpeechSegment
        {
            public int GlobalIndex { get; }
            public TimeSpan StartTime { get; }
            public TimeSpan EndTime { get; }
            public TimeSpan Duration => EndTime - StartTime;

            /// <summary>True for a "not speech" stretch, false for a mountain (suspected speech).</summary>
            public bool IsSilence { get; }

            /// <summary>Highest terrain value inside the segment.</summary>
            public float PeakLevel { get; }

            internal SpeechSegment(int globalIndex, TimeSpan startTime, TimeSpan endTime, float peakLevel, bool isSilence)
            {
                GlobalIndex = globalIndex;
                StartTime = startTime;
                EndTime = endTime;
                PeakLevel = peakLevel;
                IsSilence = isSilence;
            }
        }

        // ---------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------

        private readonly PcmAudio r_fullAudio;

        public List<SpeechSegment> Segments { get; private set; } = new List<SpeechSegment>();

        private float[] r_terrain = new float[0];
        private float[] r_floor = new float[0];
        private float[] r_localMin = new float[0];

        private List<(int start, int end)> r_zones = new List<(int, int)>();
        private List<int> r_cuts = new List<int>();            // final cuts

        public AudioTerrainMapping(PcmAudio fullAudio)
        {
            if (fullAudio == null)
                throw new ArgumentNullException(nameof(fullAudio));
            r_fullAudio = fullAudio;
        }

        public TimeSpan FrameToTime(int frame) => TimeSpan.FromMilliseconds(frame * MsPerFrame);
        public int TimeToFrame(TimeSpan time) => (int)Math.Round(time.TotalMilliseconds / MsPerFrame);
        private int Frames(float ms) => Math.Max(1, (int)Math.Round(ms / MsPerFrame));

        // ---------------------------------------------------------------------
        // Pipeline
        // ---------------------------------------------------------------------

        public void Compute()
        {
            Segments = new List<SpeechSegment>();
            r_zones = new List<(int, int)>();
            r_cuts = new List<int>();

            float[] terrain = r_fullAudio.ToPeak(MsPerFrame);
            if (terrain.Length == 0)
            {
                r_terrain = terrain;
                r_floor = new float[0];
                r_localMin = new float[0];
                BuildNavigationIndex();
                return;
            }

            int mr = Frames(MaxFilterMs);
            terrain = SlidingExtreme(terrain, mr, mr, true);
            terrain = Smooth(terrain, Frames(SmoothMs));
            r_terrain = terrain;

            float maxPeak = terrain.Max();
            float[] audible = terrain.Where(v => v > maxPeak * 0.02f).ToArray();
            float globalLoud = audible.Length > 0 ? Percentile(audible, 0.90f) : maxPeak;
            r_floor = ComputeFloor(terrain, globalLoud * MinFloorFraction);

            int vw = Frames(ValleyWindowMs);
            r_localMin = SlidingExtreme(terrain, vw, vw, false);

            r_zones = FindSpeechZones(terrain);

            foreach (var z in r_zones)
                r_cuts.AddRange(SplitZone(terrain, z.start, z.end));

            BuildSegments(terrain);
            BuildNavigationIndex();
        }

        // ---------------------------------------------------------------------
        // 1-2. Terrain helpers and noise floor
        // ---------------------------------------------------------------------

        /// <summary>
        /// Local lower bound: a low percentile of every block, then the lowest block nearby (so a long
        /// burst of speech does not raise the floor), then linear interpolation between block centres.
        /// </summary>
        private float[] ComputeFloor(float[] t, float minFloor)
        {
            int n = t.Length;
            int block = Frames(FloorBlockMs);
            int nb = (n + block - 1) / block;

            var perBlock = new float[nb];
            for (int b = 0; b < nb; b++)
            {
                int s = b * block;
                int len = Math.Min(block, n - s);
                var slice = new float[len];
                Array.Copy(t, s, slice, 0, len);
                Array.Sort(slice);
                float val = slice[Math.Min(len - 1, (int)(len * FloorPercentile))];

                // If a block is muted/digital silence, mark it as +Infinity so it doesn't drag down
                // the noise floor of neighbouring unmuted blocks for 2 seconds before/after a cut!
                perBlock[b] = val < minFloor ? float.PositiveInfinity : val;
            }

            float[] lowest = SlidingExtreme(perBlock, FloorNeighbourBlocks, FloorNeighbourBlocks, false);

            var floor = new float[n];
            for (int i = 0; i < n; i++)
            {
                float pos = (i - block / 2f) / block;
                int i0 = Math.Max(0, Math.Min(nb - 1, (int)Math.Floor(pos)));
                int i1 = Math.Min(nb - 1, i0 + 1);
                float f = Math.Max(0f, Math.Min(1f, pos - i0));

                float l0 = float.IsInfinity(lowest[i0]) ? minFloor : lowest[i0];
                float l1 = float.IsInfinity(lowest[i1]) ? minFloor : lowest[i1];
                floor[i] = Math.Max(minFloor, l0 + (l1 - l0) * f);
            }
            return floor;
        }

        /// <summary>result[i] = max (or min) of a[i-left .. i+right]. O(n).</summary>
        private static float[] SlidingExtreme(float[] a, int left, int right, bool takeMax)
        {
            int n = a.Length;
            var result = new float[n];
            var dq = new int[n];
            int head = 0, tail = 0, next = 0;

            for (int i = 0; i < n; i++)
            {
                int hi = Math.Min(n - 1, i + right);
                int lo = Math.Max(0, i - left);

                while (next <= hi)
                {
                    while (tail > head && (takeMax ? a[dq[tail - 1]] <= a[next] : a[dq[tail - 1]] >= a[next]))
                        tail--;
                    dq[tail++] = next++;
                }
                while (dq[head] < lo)
                    head++;

                result[i] = a[dq[head]];
            }
            return result;
        }

        /// <summary>Moving average, O(n).</summary>
        private static float[] Smooth(float[] a, int radius)
        {
            int n = a.Length;
            var prefix = new double[n + 1];
            for (int i = 0; i < n; i++)
                prefix[i + 1] = prefix[i] + a[i];

            var o = new float[n];
            for (int i = 0; i < n; i++)
            {
                int s = Math.Max(0, i - radius), e = Math.Min(n - 1, i + radius);
                o[i] = (float)((prefix[e + 1] - prefix[s]) / (e - s + 1));
            }
            return o;
        }

        private static float Percentile(float[] a, float p)
        {
            var sorted = (float[])a.Clone();
            Array.Sort(sorted);
            return sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * p))];
        }

        // ---------------------------------------------------------------------
        // 3. Speech zones
        // ---------------------------------------------------------------------

        private List<(int start, int end)> FindSpeechZones(float[] t)
        {
            int n = t.Length;

            // Runs above the threshold
            var runs = new List<(int start, int end)>();
            int start = -1;
            for (int i = 0; i < n; i++)
            {
                bool on = t[i] > r_floor[i] * SpeechRatio;
                if (on && start < 0)
                {
                    start = i;
                }
                else if (!on && start >= 0)
                {
                    runs.Add((start, i));
                    start = -1;
                }
            }
            if (start >= 0)
                runs.Add((start, n));

            // Small gaps do not split a zone
            int maxGap = Frames(MaxGapMs);
            var merged = new List<(int start, int end)>();
            foreach (var r in runs)
            {
                if (merged.Count > 0 && r.start - merged[merged.Count - 1].end < maxGap)
                    merged[merged.Count - 1] = (merged[merged.Count - 1].start, r.end);
                else
                    merged.Add(r);
            }

            // Keep real speech only: long enough and reaching high enough above the floor
            int minZone = Frames(MinZoneMs);
            var zones = new List<(int start, int end)>();
            foreach (var z in merged)
            {
                if (z.end - z.start < minZone)
                    continue;

                float best = 0f;
                for (int k = z.start; k < z.end; k++)
                    best = Math.Max(best, t[k] / r_floor[k]);
                if (best >= PeakRatio)
                    zones.Add(z);
            }

            // Include the slopes: slide each edge outward while the terrain keeps going down
            int slopeMax = Frames(SlopeMaxMs);
            int slopeExtraStart = Frames(SlopeExtraStartMs);
            int slopeExtraEnd = Frames(SlopeExtraEndMs);
            var result = new List<(int start, int end)>();
            int prevEnd = 0;
            for (int zi = 0; zi < zones.Count; zi++)
            {
                int s = zones[zi].start;
                int e = zones[zi].end;
                int nextStart = zi + 1 < zones.Count ? zones[zi + 1].start : n;

                int limS = Math.Max(prevEnd, s - slopeMax);
                while (s > limS && t[s - 1] <= t[s] && t[s - 1] > r_floor[s - 1] * SlopeEndRatio)
                    s--;

                s = Math.Max(prevEnd, s - slopeExtraStart);

                int limE = Math.Min(nextStart, e + slopeMax);
                while (e < limE && e < n && t[e] <= t[e - 1] && t[e] > r_floor[e] * SlopeEndRatio)
                    e++;

                e = Math.Min(nextStart, e + slopeExtraEnd);

                result.Add((s, e));
                prevEnd = e;
            }
            return result;
        }

        // ---------------------------------------------------------------------
        // 4-5. Cuts inside a zone
        // ---------------------------------------------------------------------

        private class Mountain
        {
            public int Start;
            public int End;     // = the cut that follows
            public float Peak;
        }

        /// <summary>Returns the final cuts inside the zone [zs, ze[ (sorted).</summary>
        private List<int> SplitZone(float[] t, int zs, int ze)
        {
            int w = Frames(ValleyWindowMs);

            // Candidates: local minima
            var cand = new List<int>();
            for (int i = zs + 1; i < ze - 1; i++)
            {
                if (t[i] > r_localMin[i])
                    continue;
                if (cand.Count > 0 && i - cand[cand.Count - 1] < w)
                {
                    if (t[i] < t[cand[cand.Count - 1]])
                        cand[cand.Count - 1] = i;
                }
                else
                {
                    cand.Add(i);
                }
            }

            var mts = new List<Mountain>(cand.Count + 1);
            int s = zs;
            foreach (int c in cand)
            {
                mts.Add(new Mountain { Start = s, End = c, Peak = Peak(t, s, c) });
                s = c;
            }
            mts.Add(new Mountain { Start = s, End = ze, Peak = Peak(t, s, ze) });

            // Valleys that do not reach down are removed, the shallowest first
            while (mts.Count > 1)
            {
                int worst = -1;
                float worstRatio = ValleyReach;
                for (int i = 0; i < mts.Count - 1; i++)
                {
                    float r = ValleyRatio(t, mts[i], mts[i + 1]);
                    if (r > worstRatio)
                    {
                        worstRatio = r;
                        worst = i;
                    }
                }
                if (worst < 0)
                    break;
                Merge(mts, worst);
            }

            // Final cuts, centred on the flat bottom of the valley when there is one
            var cuts = new List<int>();
            int minFlatGap = Frames(MinZoneMs); // e.g. 80ms: if the valley floor is wider than this, cut at BOTH feet
            for (int i = 0; i < mts.Count - 1; i++)
            {
                int lo = cuts.Count > 0 ? Math.Max(mts[i].Start, cuts[cuts.Count - 1] + 1) : mts[i].Start;
                int hi = mts[i + 1].End;
                if (lo >= hi)
                    continue;

                int c = Math.Max(lo, Math.Min(hi - 1, mts[i].End));
                var (leftFoot, rightFoot) = ValleyFeet(t, c, lo, hi);

                if (rightFoot - leftFoot >= minFlatGap)
                {
                    if (cuts.Count == 0 || leftFoot > cuts[cuts.Count - 1])
                        cuts.Add(leftFoot);
                    if (rightFoot > cuts[cuts.Count - 1])
                        cuts.Add(rightFoot);
                }
                else
                {
                    int mid = (leftFoot + rightFoot) / 2;
                    if (cuts.Count == 0 || mid > cuts[cuts.Count - 1])
                        cuts.Add(mid);
                }
            }
            return cuts;
        }

        private (int left, int right) ValleyFeet(float[] t, int c, int lo, int hi)
        {
            // Anything within 20% of the valley bottom (or below the speech threshold) is part of the flat valley floor
            float level = Math.Max(t[c] * 1.2f + 1e-6f, r_floor[c] * SpeechRatio);
            int l = c, r = c;
            while (l - 1 > lo && t[l - 1] <= level) l--;
            while (r + 1 < hi - 1 && t[r + 1] <= level) r++;
            return (l, r);
        }

        private float ValleyRatio(float[] t, Mountain a, Mountain b)
        {
            int c = a.End;
            float lower = Math.Min(a.Peak, b.Peak);
            float denom = lower - r_floor[c];
            if (denom <= 1e-9f)
                return 1f;
            return Math.Max(0f, Math.Min(1f, (t[c] - r_floor[c]) / denom));
        }

        private static void Merge(List<Mountain> mts, int i)
        {
            mts[i].End = mts[i + 1].End;
            mts[i].Peak = Math.Max(mts[i].Peak, mts[i + 1].Peak);
            mts.RemoveAt(i + 1);
        }

        private static float Peak(float[] t, int from, int to)
        {
            int a = Math.Max(0, from);
            int b = Math.Min(t.Length - 1, to - 1);
            float peak = 0f;
            for (int k = a; k <= b; k++)
                if (t[k] > peak) peak = t[k];
            return peak;
        }

        // ---------------------------------------------------------------------
        // Segments
        // ---------------------------------------------------------------------

        private void BuildSegments(float[] t)
        {
            var raw = new List<SpeechSegment>();
            int pos = 0;
            int cutIdx = 0;

            foreach (var z in r_zones)
            {
                if (z.start > pos)
                    raw.Add(new SpeechSegment(raw.Count, FrameToTime(pos), FrameToTime(z.start), Peak(t, pos, z.start), true));

                int s = z.start;
                while (cutIdx < r_cuts.Count && r_cuts[cutIdx] < z.end)
                {
                    int c = r_cuts[cutIdx++];
                    AddZoneSegment(raw, t, s, c);
                    s = c;
                }
                AddZoneSegment(raw, t, s, z.end);
                pos = z.end;
            }

            if (t.Length > pos)
                raw.Add(new SpeechSegment(raw.Count, FrameToTime(pos), FrameToTime(t.Length), Peak(t, pos, t.Length), true));

            // Merge any consecutive silence segments first so each silence stretch after a mountain is contiguous
            var merged = new List<SpeechSegment>(raw.Count);
            foreach (var seg in raw)
            {
                if (seg.IsSilence && merged.Count > 0 && merged[merged.Count - 1].IsSilence)
                {
                    var prev = merged[merged.Count - 1];
                    merged[merged.Count - 1] = new SpeechSegment(
                        prev.GlobalIndex,
                        prev.StartTime,
                        seg.EndTime,
                        Math.Max(prev.PeakLevel, seg.PeakLevel),
                        true);
                }
                else
                {
                    merged.Add(new SpeechSegment(merged.Count, seg.StartTime, seg.EndTime, seg.PeakLevel, seg.IsSilence));
                }
            }

            // Go back and split long silence segments following a mountain into [<start>, <start> + 100ms] and [<start> + 100ms, <end>]
            var tailSplit = TimeSpan.FromMilliseconds(SilenceTailSplitMs);
            var finalSegments = new List<SpeechSegment>(merged.Count + 16);
            for (int i = 0; i < merged.Count; i++)
            {
                var seg = merged[i];
                bool followsMountain = i > 0 && !merged[i - 1].IsSilence;
                if (seg.IsSilence && followsMountain && SilenceTailSplitMs > 0f && seg.Duration > tailSplit)
                {
                    TimeSpan splitTime = seg.StartTime + tailSplit;
                    int sFrame = TimeToFrame(seg.StartTime);
                    int splitFrame = TimeToFrame(splitTime);
                    int eFrame = TimeToFrame(seg.EndTime);

                    finalSegments.Add(new SpeechSegment(
                        finalSegments.Count,
                        seg.StartTime,
                        splitTime,
                        Peak(t, sFrame, splitFrame),
                        true));

                    finalSegments.Add(new SpeechSegment(
                        finalSegments.Count,
                        splitTime,
                        seg.EndTime,
                        Peak(t, splitFrame, eFrame),
                        true));
                }
                else
                {
                    finalSegments.Add(new SpeechSegment(
                        finalSegments.Count,
                        seg.StartTime,
                        seg.EndTime,
                        seg.PeakLevel,
                        seg.IsSilence));
                }
            }

            Segments = finalSegments;
        }

        private void AddZoneSegment(List<SpeechSegment> all, float[] t, int s, int e)
        {
            if (e <= s)
                return;
            float peak = Peak(t, s, e);
            int mid = (s + e) / 2;
            bool isSilence = peak < r_floor[mid] * PeakRatio;
            all.Add(new SpeechSegment(all.Count, FrameToTime(s), FrameToTime(e), peak, isSilence));
        }

        // ---------------------------------------------------------------------
        // Navigation: "jump mountains, stop at a dip" (unchanged behaviour)
        // ---------------------------------------------------------------------

        private List<SpeechSegment> r_mountains = new List<SpeechSegment>();
        private List<TimeSpan> r_mountainStarts = new List<TimeSpan>();
        private List<TimeSpan> r_mountainEnds = new List<TimeSpan>();
        private List<TimeSpan> r_segStarts = new List<TimeSpan>();
        private List<TimeSpan> r_segEnds = new List<TimeSpan>();

        private void BuildNavigationIndex()
        {
            r_mountains = Segments.Where(s => !s.IsSilence).ToList();
            r_mountainStarts = r_mountains.Select(s => s.StartTime).ToList();
            r_mountainEnds = r_mountains.Select(s => s.EndTime).ToList();

            // Start dips only ever land on the start of a mountain (skipping all silence segments)
            r_segStarts = r_mountainStarts;

            // End dips land on the end of a mountain OR the +100ms silence cut right after a mountain
            r_segEnds = new List<TimeSpan>(r_mountains.Count * 2);
            for (int i = 0; i < Segments.Count; i++)
            {
                var seg = Segments[i];
                if (!seg.IsSilence)
                {
                    r_segEnds.Add(seg.EndTime);
                }
                else if (i > 0 && !Segments[i - 1].IsSilence && i + 1 < Segments.Count && Segments[i + 1].IsSilence)
                {
                    r_segEnds.Add(seg.EndTime);
                }
            }
        }

        private List<TimeSpan> DipList(BoundarySide side) => side == BoundarySide.Start ? r_segStarts : r_segEnds;

        /// <summary>True when moving the boundary on 'side' in direction 'dir' makes its time later.</summary>
        public static bool MovesLater(BoundarySide side, MoveDirection dir)
            => (side == BoundarySide.Start) == (dir == MoveDirection.Inward);

        public TimeSpan? FindDip(TimeSpan from, BoundarySide side, MoveDirection dir, DipSearch options = null)
        {
            options = options ?? new DipSearch();
            var list = DipList(side);
            bool later = MovesLater(side, dir);

            int i;
            if (later)
                i = options.Inclusive ? LastIndexLess(list, from) + 1 : FirstIndexGreater(list, from);
            else
                i = options.Inclusive ? FirstIndexGreater(list, from) - 1 : LastIndexLess(list, from);

            for (; i >= 0 && i < list.Count; i += later ? 1 : -1)
            {
                var dip = list[i];

                if (options.Limit.HasValue && (later ? dip > options.Limit.Value : dip < options.Limit.Value))
                    return null;

                if ((dip - from).Duration() >= options.MinJump)
                    return dip;

                if (options.StopAtSpeechEdge && IsSpeechEdge(dip))
                    return dip;
            }
            return null;
        }

        public bool IsSpeechEdge(TimeSpan dip)
        {
            bool before = MountainEndingAt(dip) != null;
            bool after = MountainStartingAt(dip) != null;
            return before != after;
        }

        public bool IsSilent(TimeSpan time) => MountainAt(time) == null;

        /// <summary>
        /// Returns true if any speech mountain overlaps the open interval (from, to).
        /// </summary>
        public bool HasSpeechBetween(TimeSpan from, TimeSpan to)
        {
            if (to <= from || r_mountains == null || r_mountains.Count == 0)
                return false;
            int i = LastIndexLess(r_mountainStarts, to);
            return i >= 0 && r_mountainEnds[i] > from;
        }

        private SpeechSegment MountainAt(TimeSpan time)
        {
            int i = LastIndexLess(r_mountainStarts, time + TimeSpan.FromTicks(1));
            return (i < 0 || time >= r_mountainEnds[i]) ? null : r_mountains[i];
        }

        private SpeechSegment MountainStartingAt(TimeSpan time)
        {
            int i = LastIndexLess(r_mountainStarts, time + TimeSpan.FromTicks(1));
            return (i >= 0 && r_mountainStarts[i] == time) ? r_mountains[i] : null;
        }

        private SpeechSegment MountainEndingAt(TimeSpan time)
        {
            int i = LastIndexLess(r_mountainEnds, time + TimeSpan.FromTicks(1));
            return (i >= 0 && r_mountainEnds[i] == time) ? r_mountains[i] : null;
        }

        private static int FirstIndexGreater(List<TimeSpan> l, TimeSpan v)
        {
            int lo = 0, hi = l.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (l[mid] > v) hi = mid; else lo = mid + 1;
            }
            return lo;
        }

        private static int LastIndexLess(List<TimeSpan> l, TimeSpan v)
        {
            int lo = 0, hi = l.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (l[mid] < v) lo = mid + 1; else hi = mid;
            }
            return lo - 1;
        }

        // ---------------------------------------------------------------------
        // Output
        // ---------------------------------------------------------------------

        /// <summary>Mountains are labelled A, B, C... (so neighbours differ), post-mountain +100ms silence tail is labelled "-".</summary>
        public IEnumerable<Subtitle> ToSegmentSubtitles()
        {
            var oneMs = TimeSpan.FromMilliseconds(1);
            int mountainIndex = 0;
            for (int i = 0; i < Segments.Count; i++)
            {
                var seg = Segments[i];
                bool isSilenceTail = seg.IsSilence && i > 0 && !Segments[i - 1].IsSilence && i + 1 < Segments.Count && Segments[i + 1].IsSilence;
                if (!seg.IsSilence || isSilenceTail)
                {
                    string label = seg.IsSilence ? "-" : ((char)('A' + (mountainIndex++ % 26))).ToString();
                    TimeSpan endTime = (i < Segments.Count - 1 && seg.Duration > oneMs)
                        ? seg.EndTime - oneMs
                        : seg.EndTime;

                    yield return new Subtitle(seg.StartTime, endTime, new[] { label });
                }
            }
        }
    }
}
