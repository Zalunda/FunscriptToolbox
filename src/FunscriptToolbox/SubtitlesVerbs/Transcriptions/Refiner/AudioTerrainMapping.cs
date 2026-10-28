using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
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

        public int MsPerFrame { get; set; } = 1;

        // Fixed internals (not worth exposing)
        private const float MaxFilterMs = 6f;          // removes pitch teeth
        private const float SmoothMs = 3f;
        private const float FloorBlockMs = 250f;       // block size for the noise floor
        private const int FloorNeighbourBlocks = 8;    // lowest block within +/- 8 blocks (= 2 s)
        private const float FloorPercentile = 0.10f;
        private const float MinFloorFraction = 0.01f;  // floor never below 1% of the typical loud level
        private const float MaxGapMs = 150f;           // gaps shorter than this do not split a zone
        private const float MinZoneMs = 80f;
        private const float ValleyWindowMs = 15f;      // local-minimum window
        private const float SlopeMaxMs = 150f;         // how far an edge may slide down a slope
        private const float SlopeEndRatio = 1.2f;      // ...or until terrain is within 20% of the floor

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

        // Debug info
        private List<(int start, int end)> r_zones = new List<(int, int)>();
        private List<int> r_cuts = new List<int>();            // final cuts
        // Shallow valleys: where they are, and the terrain level at which they would have been accepted
        private List<(int frame, float limit)> r_rejected = new List<(int, float)>();
        // Candidate zones that failed (too short / peak too low), with the reason
        private List<(int start, int end, string reason)> r_rejectedZones = new List<(int, int, string)>();

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
            r_rejected = new List<(int, float)>();
            r_rejectedZones = new List<(int, int, string)>();

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
                {
                    r_rejectedZones.Add((z.start, z.end, $"short {(z.end - z.start) * MsPerFrame} ms (min {MinZoneMs:0})"));
                    continue;
                }

                float best = 0f;
                for (int k = z.start; k < z.end; k++)
                    best = Math.Max(best, t[k] / r_floor[k]);
                if (best >= PeakRatio)
                    zones.Add(z);
                else
                    r_rejectedZones.Add((z.start, z.end, $"peak {best:0.0}x floor (needs {PeakRatio:0.#}x)"));
            }

            // Include the slopes: slide each edge outward while the terrain keeps going down
            int slopeMax = Frames(SlopeMaxMs);
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

                int limE = Math.Min(nextStart, e + slopeMax);
                while (e < limE && e < n && t[e] <= t[e - 1] && t[e] > r_floor[e] * SlopeEndRatio)
                    e++;

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
                int cutFrame = mts[worst].End;
                float lowerPeak = Math.Min(mts[worst].Peak, mts[worst + 1].Peak);
                float limit = r_floor[cutFrame] + ValleyReach * (lowerPeak - r_floor[cutFrame]);
                r_rejected.Add((cutFrame, Math.Max(limit, r_floor[cutFrame])));
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
            var all = new List<SpeechSegment>();
            int pos = 0;
            int cutIdx = 0;

            foreach (var z in r_zones)
            {
                if (z.start > pos)
                    all.Add(new SpeechSegment(all.Count, FrameToTime(pos), FrameToTime(z.start), Peak(t, pos, z.start), true));

                int s = z.start;
                while (cutIdx < r_cuts.Count && r_cuts[cutIdx] < z.end)
                {
                    int c = r_cuts[cutIdx++];
                    AddZoneSegment(all, t, s, c);
                    s = c;
                }
                AddZoneSegment(all, t, s, z.end);
                pos = z.end;
            }

            if (t.Length > pos)
                all.Add(new SpeechSegment(all.Count, FrameToTime(pos), FrameToTime(t.Length), Peak(t, pos, t.Length), true));

            Segments = all;
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

        /// <summary>
        /// Says what the algorithm saw at 'time': terrain, floor, threshold, and why it is (or is not) speech.
        /// Call after Compute(). Handy for "why was that mountain ignored?".
        /// </summary>
        public string ExplainAt(TimeSpan time)
        {
            if (r_terrain == null || r_terrain.Length == 0)
                return "no data";

            int f = Math.Max(0, Math.Min(r_terrain.Length - 1, TimeToFrame(time)));
            float t = r_terrain[f];
            float fl = r_floor[f];

            string verdict;
            if (r_zones.Any(z => f >= z.start && f < z.end))
                verdict = "inside a speech zone";
            else
            {
                var rej = r_rejectedZones.Where(z => f >= z.start && f < z.end).ToList();
                verdict = rej.Count > 0
                    ? "candidate zone rejected: " + rej[0].reason
                    : t <= fl * SpeechRatio
                        ? "below the speech threshold"
                        : "above the threshold but outside every zone (edge / merged gap)";
            }

            var seg = Segments.FirstOrDefault(s => time >= s.StartTime && time < s.EndTime);
            string segText = seg == null
                ? ""
                : $" | segment #{seg.GlobalIndex} {(seg.IsSilence ? "non-speech" : "mountain")} {seg.StartTime:m\\:ss\\.fff}-{seg.EndTime:m\\:ss\\.fff}";

            return $"terrain={t:0.0000} floor={fl:0.0000} threshold={fl * SpeechRatio:0.0000} " +
                   $"({t / fl:0.0}x floor, needs {SpeechRatio:0.#}x) -> {verdict}{segText}";
        }

        /// <summary>Highest terrain value between two times (0 when empty). Call after Compute().</summary>
        public float GetPeakLevel(TimeSpan from, TimeSpan to)
        {
            if (to <= from || r_terrain == null || r_terrain.Length == 0)
                return 0f;
            return Peak(r_terrain, TimeToFrame(from), TimeToFrame(to));
        }

        // ---------------------------------------------------------------------
        // Navigation: "jump mountains, stop at a dip" (unchanged behaviour)
        // ---------------------------------------------------------------------

        private List<SpeechSegment> r_mountains = new List<SpeechSegment>();
        private List<TimeSpan> r_segStarts = new List<TimeSpan>();
        private List<TimeSpan> r_segEnds = new List<TimeSpan>();

        private void BuildNavigationIndex()
        {
            r_mountains = Segments.Where(s => !s.IsSilence).ToList();
            r_segStarts = r_mountains.Select(s => s.StartTime).ToList();
            r_segEnds = r_mountains.Select(s => s.EndTime).ToList();
        }

        private List<TimeSpan> DipList(BoundarySide side) => side == BoundarySide.Start ? r_segStarts : r_segEnds;

        private static bool MovesLater(BoundarySide side, MoveDirection dir)
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

                if (options.StopAtSpeechEdge && IsSpeechEdge(dip, options.SignificantLevel))
                    return dip;
            }
            return null;
        }

        public IEnumerable<TimeSpan> GetDips(BoundarySide side, TimeSpan from, TimeSpan to)
        {
            var list = DipList(side);
            for (int i = LastIndexLess(list, from) + 1; i < list.Count && list[i] <= to; i++)
                yield return list[i];
        }

        public bool IsSpeechEdge(TimeSpan dip, float significantLevel)
        {
            bool before = IsSignificant(MountainEndingAt(dip), significantLevel);
            bool after = IsSignificant(MountainStartingAt(dip), significantLevel);
            return before != after;
        }

        public bool IsInsignificant(TimeSpan time, float significantLevel)
            => !IsSignificant(MountainAt(time), significantLevel);

        public bool IsSilent(TimeSpan time) => MountainAt(time) == null;

        private static bool IsSignificant(SpeechSegment seg, float significantLevel)
            => seg != null && seg.PeakLevel >= significantLevel;

        private SpeechSegment MountainAt(TimeSpan time)
        {
            int i = LastIndexLess(r_segStarts, time + TimeSpan.FromTicks(1));
            return (i < 0 || time >= r_segEnds[i]) ? null : r_mountains[i];
        }

        private SpeechSegment MountainStartingAt(TimeSpan time)
        {
            int i = LastIndexLess(r_segStarts, time + TimeSpan.FromTicks(1));
            return (i >= 0 && r_segStarts[i] == time) ? r_mountains[i] : null;
        }

        private SpeechSegment MountainEndingAt(TimeSpan time)
        {
            int i = LastIndexLess(r_segEnds, time + TimeSpan.FromTicks(1));
            return (i >= 0 && r_segEnds[i] == time) ? r_mountains[i] : null;
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

        /// <summary>Mountains are labelled A, B, C... (so neighbours differ), non-speech "-".</summary>
        public IEnumerable<Subtitle> ToSegmentSubtitles()
        {
            var oneMs = TimeSpan.FromMilliseconds(1);
            int mountainIndex = 0;
            for (int i = 0; i < Segments.Count; i++)
            {
                var seg = Segments[i];
                if (!seg.IsSilence)
                {
                    string label = seg.IsSilence ? "-" : ((char)('A' + (mountainIndex++ % 26))).ToString();
                    TimeSpan endTime = (i < Segments.Count - 1 && seg.Duration > oneMs)
                        ? seg.EndTime - oneMs
                        : seg.EndTime;

                    yield return new Subtitle(seg.StartTime, endTime, new[] { label });
                }
            }
        }

        /// <summary>
        /// Renders the terrain and what the algorithm decided. Call after Compute().
        ///   - green fill        : terrain
        ///   - cyan line         : local noise floor
        ///   - yellow line       : speech threshold (floor * SpeechRatio)
        ///   - lighter background + green box : suspected speech zone (box sits above its peak)
        ///   - orange circles    : kept cuts (valleys that reach down)
        ///   - small grey dots   : valleys rejected because too shallow, with a hollow circle below it
        ///                         at the level where the valley would have been accepted
        ///   - red box + text    : candidate zones rejected (too short / peak too low)
        ///   - purple circles    : cuts removed by the "silence, small mountain, bigger mountain" exception
        ///   - dark red lines    : segment boundaries
        /// </summary>
        public void SaveDebugImage(string path, TimeSpan start, TimeSpan duration,
                                   float msPerPixel = 3f, int height = 400)
        {
            if (r_terrain == null || r_terrain.Length == 0)
                return;

            int w = (int)(duration.TotalMilliseconds / msPerPixel);
            if (w <= 1)
                return;

            int f0 = Math.Max(0, TimeToFrame(start));
            double startMs = f0 * (double)MsPerFrame;
            int totalH = height + 20;
            int framesPerPx = Math.Max(1, (int)Math.Round(msPerPixel / MsPerFrame));

            var col = new float[w];
            float peak = 0.0001f;
            for (int x = 0; x < w; x++)
            {
                int a = f0 + (int)(x * msPerPixel / MsPerFrame);
                float m = 0;
                for (int k = a; k <= a + framesPerPx && k < r_terrain.Length; k++)
                    m = Math.Max(m, r_terrain[k]);
                col[x] = m;
                peak = Math.Max(peak, m);
            }

            int ToX(int frame) => (int)((frame * (double)MsPerFrame - startMs) / msPerPixel);
            int ToXms(double ms) => (int)((ms - startMs) / msPerPixel);
            int ToY(float v) => height - (int)(Math.Min(v, peak) / peak * (height - 10));

            using (var bmp = new Bitmap(w, totalH))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);

                // Zones: lighter background + box above the peak
                using (var bg = new SolidBrush(Color.FromArgb(45, 45, 45)))
                using (var boxPen = new Pen(Color.FromArgb(60, 170, 90)))
                    foreach (var z in r_zones)
                    {
                        int xs = Math.Max(0, ToX(z.start));
                        int xe = Math.Min(w, ToX(z.end));
                        if (xe <= xs) continue;
                        g.FillRectangle(bg, xs, 0, xe - xs, height);
                        int yTop = Math.Max(0, ToY(Peak(r_terrain, z.start, z.end)) - 14);
                        g.DrawRectangle(boxPen, xs, yTop, xe - xs, 8);
                    }

                // Terrain
                using (var pen = new Pen(Color.FromArgb(170, 230, 60)))
                    for (int x = 0; x < w; x++)
                        g.DrawLine(pen, x, ToY(col[x]), x, height);

                // Floor and threshold
                using (var floorPen = new Pen(Color.Cyan))
                using (var thrPen = new Pen(Color.Gold))
                {
                    int px = 0;
                    int pf = 0, pt = 0;
                    for (int x = 0; x < w; x++)
                    {
                        int fr = Math.Min(r_floor.Length - 1, f0 + (int)(x * msPerPixel / MsPerFrame));
                        int yf = ToY(r_floor[fr]);
                        int yt = ToY(r_floor[fr] * SpeechRatio);
                        if (x > 0)
                        {
                            g.DrawLine(floorPen, px, pf, x, yf);
                            g.DrawLine(thrPen, px, pt, x, yt);
                        }
                        px = x; pf = yf; pt = yt;
                    }
                }

                // Segment boundaries
                using (var pen = new Pen(Color.FromArgb(150, 200, 40, 40)))
                    foreach (var seg in Segments)
                    {
                        int x = ToXms(seg.StartTime.TotalMilliseconds);
                        if (x >= 0 && x < w) g.DrawLine(pen, x, 0, x, height);
                    }

                // Candidate zones that were rejected (red box + reason)
                using (var rejPen = new Pen(Color.IndianRed))
                    foreach (var z in r_rejectedZones)
                    {
                        int xs = Math.Max(0, ToX(z.start));
                        int xe = Math.Min(w, ToX(z.end));
                        if (xe <= xs) continue;
                        int yTop = Math.Max(0, ToY(Peak(r_terrain, z.start, z.end)) - 14);
                        g.DrawRectangle(rejPen, xs, yTop, xe - xs, 8);
                        g.DrawString(z.reason, SystemFonts.DefaultFont, Brushes.IndianRed, xs, Math.Max(0, yTop - 14));
                    }

                // Rejected valleys: grey dot on the valley, hollow dot where it would have been accepted
                using (var limitPen = new Pen(Color.Gainsboro))
                    foreach (var rj in r_rejected)
                    {
                        int x = ToX(rj.frame);
                        if (x < 0 || x >= w) continue;
                        int yValley = ToY(r_terrain[rj.frame]);
                        int yLimit = ToY(rj.limit);
                        g.DrawLine(Pens.DimGray, x, yValley, x, yLimit);
                        DrawDot(g, x, yValley, 2, Brushes.Gray, w);
                        g.DrawEllipse(limitPen, x - 3, yLimit - 3, 6, 6);
                    }

                // Kept cuts
                foreach (int c in r_cuts)
                    DrawDot(g, ToX(c), ToY(r_terrain[c]), 5, Brushes.Orange, w);

                // Time ticks every 500 ms
                for (double ms = Math.Ceiling(startMs / 500) * 500; ms < startMs + duration.TotalMilliseconds; ms += 500)
                {
                    int x = ToXms(ms);
                    g.DrawLine(Pens.White, x, totalH - 10, x, totalH);
                    g.DrawString(TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss\.fff"),
                                 SystemFonts.DefaultFont, Brushes.White, x + 2, totalH - 14);
                }

                bmp.Save(path, ImageFormat.Jpeg);
            }
        }

        private static void DrawDot(Graphics g, int x, int y, int radius, Brush brush, int width)
        {
            if (x < -radius || x > width + radius) return;
            g.FillEllipse(brush, x - radius, y - radius, radius * 2, radius * 2);
        }
    }
}
