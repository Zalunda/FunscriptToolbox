using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    /// <summary>Which boundary of a clip is moving.</summary>
    public enum BoundarySide
    {
        Start,
        End
    }

    /// <summary>Outward = the clip gets longer, Inward = the clip gets shorter.</summary>
    public enum MoveDirection
    {
        Outward,
        Inward
    }

    /// <summary>Options for AudioTerrainMappingPhysicBased.FindDip.</summary>
    public class DipSearch
    {
        /// <summary>Minimum distance between 'from' and the returned dip (0 = the next dip).</summary>
        public TimeSpan MinJump { get; set; } = TimeSpan.Zero;

        /// <summary>Mountains with a peak below this level are insignificant (0 = every mountain is significant).</summary>
        public float SignificantLevel { get; set; } = 0f;

        /// <summary>When true, the search may stop before MinJump on a speech edge (see IsSpeechEdge).</summary>
        public bool StopAtSpeechEdge { get; set; } = false;

        /// <summary>When true, 'from' itself is an acceptable answer if it is a dip ("snap").</summary>
        public bool Inclusive { get; set; } = false;

        /// <summary>The search never returns a dip beyond this time (in the direction of the move).</summary>
        public TimeSpan? Limit { get; set; }
    }

    /// <summary>
    /// Splits speech into islands (separated by silence) and cuts each island at every
    /// place where a rolling ball can come to rest in the audio-energy terrain.
    ///
    /// Design rule: NOTHING is hidden by ranking or filtering. The number of cuts is
    /// controlled only by:
    ///   1. terrain shaping   (MaxFilterRadiusMs, SmoothRadiusMs) - which dips exist
    ///   2. BallRadiusMs                                          - which dips the ball falls into
    ///   3. MinDistanceTraveledMs                                 - balls that barely moved are discarded
    /// </summary>
    public class AudioTerrainMappingPhysicBased
    {
        // ---------------------------------------------------------------------
        // Tunable parameters
        // ---------------------------------------------------------------------

        public int MsPerFrame { get; set; } = 1;                 // ms per terrain sample

        // Terrain shaping (in ms so they stay valid if MsPerFrame changes)
        public float MaxFilterRadiusMs { get; set; } = 6f;       // removes pitch-period "teeth" (running max)
        public float SmoothRadiusMs { get; set; } = 3f;          // then smooths the envelope

        // Physics
        public float BallRadiusMs { get; set; } = 5f;            // main resolution knob: bigger = fewer cuts
        public float DropIntervalMs { get; set; } = 1f;          // spacing between ball drops
        public float MinDistanceTraveledMs { get; set; } = 5f;   // balls that move less than this are discarded
        public float MaxRollMs { get; set; } = 2000f;            // a ball still rolling after this is not "at rest"
        public float DropFloorFactor { get; set; } = 0.3f;       // balls are only dropped where terrain >= vocalFloor * factor

        // Island layout
        public TimeSpan MinInterIslandSilence { get; set; } = TimeSpan.FromMilliseconds(300);

        // ---------------------------------------------------------------------
        // Result types
        // ---------------------------------------------------------------------

        public class SpeechIsland
        {
            public int Index { get; }
            public TimeSpan StartTime { get; }
            public TimeSpan EndTime { get; }
            public TimeSpan Duration => EndTime - StartTime;
            public IReadOnlyList<TimeSpan> InternalBreaks { get; }
            public IReadOnlyList<SpeechSegment> Segments { get; }

            public char DisplayLetter => (char)('A' + (Index % 26));

            internal SpeechIsland(
                int index,
                TimeSpan startTime,
                TimeSpan endTime,
                List<TimeSpan> internalBreaks,
                List<SpeechSegment> segments)
            {
                Index = index;
                StartTime = startTime;
                EndTime = endTime;
                InternalBreaks = internalBreaks;
                Segments = segments;
            }
        }

        public class SpeechSegment
        {
            public int GlobalIndex { get; }
            public int IslandIndex { get; }
            public int IndexInIsland { get; }
            public TimeSpan StartTime { get; }
            public TimeSpan EndTime { get; }
            public TimeSpan Duration => EndTime - StartTime;
            public SpeechIsland Island { get; internal set; }

            /// <summary>Highest terrain value inside the segment ("height of the mountain").</summary>
            public float PeakLevel { get; }

            internal SpeechSegment(
                int globalIndex,
                int islandIndex,
                int indexInIsland,
                TimeSpan startTime,
                TimeSpan endTime,
                float peakLevel)
            {
                GlobalIndex = globalIndex;
                IslandIndex = islandIndex;
                IndexInIsland = indexInIsland;
                StartTime = startTime;
                EndTime = endTime;
                PeakLevel = peakLevel;
            }
        }

        // ---------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------

        private readonly PcmAudio r_fullAudio;
        public List<SpeechIsland> Islands { get; private set; }
        public List<SpeechSegment> Segments { get; private set; }

        // Kept for SaveDebugImage
        private float[] r_terrain;
        private readonly List<(int drop, int land, bool kept)> r_rolls = new List<(int, int, bool)>();
        private List<int> r_breaks = new List<int>();

        public AudioTerrainMappingPhysicBased(PcmAudio fullAudio)
        {
            if (fullAudio == null)
                throw new ArgumentNullException(nameof(fullAudio));

            r_fullAudio = fullAudio;
        }

        public TimeSpan FrameToTime(int frame) => TimeSpan.FromMilliseconds(frame * MsPerFrame);
        public int TimeToFrame(TimeSpan time) => (int)Math.Round(time.TotalMilliseconds / MsPerFrame);

        // ---------------------------------------------------------------------
        // Pipeline
        // ---------------------------------------------------------------------

        public void Compute()
        {
            // 1. Terrain: RMS, then remove pitch teeth (running max), then smooth
            float[] terrain = r_fullAudio.ToRms(MsPerFrame);
            if (terrain.Length == 0)
            {
                r_terrain = terrain;
                r_rolls.Clear();
                r_breaks = new List<int>();
                Islands = new List<SpeechIsland>();
                Segments = new List<SpeechSegment>();
                BuildNavigationIndex();
                return;
            }

            terrain = MaxFilter(terrain, MsToFrames(MaxFilterRadiusMs));
            terrain = Smooth(terrain, MsToFrames(SmoothRadiusMs));
            r_terrain = terrain;

            // 2. Floors (silence vs. speech)
            CalibrateFloors(terrain, out float silenceFloor, out float vocalFloor);

            // 3. Drop balls and let them roll
            List<int> landings = SimulateBallRolls(terrain, vocalFloor);

            // 4. Every distinct resting place is a cut
            int radiusSamples = Math.Max(1, MsToFrames(BallRadiusMs));
            r_breaks = ResolveRestingPlaces(landings, radiusSamples);

            // 5. Build islands and segments
            BuildIslandsAndSegments(
                terrain,
                r_breaks,
                silenceFloor,
                out List<SpeechIsland> islands,
                out List<SpeechSegment> segments);

            Islands = islands;
            Segments = segments;
            BuildNavigationIndex();
        }

        private int MsToFrames(float ms) => (int)Math.Round(ms / MsPerFrame);

        // ---------------------------------------------------------------------
        // Terrain shaping
        // ---------------------------------------------------------------------

        /// <summary>Running max: removes the downward "teeth" caused by the voice pitch period.</summary>
        private static float[] MaxFilter(float[] a, int r)
        {
            var o = new float[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                int s = Math.Max(0, i - r), e = Math.Min(a.Length - 1, i + r);
                float m = 0;
                for (int k = s; k <= e; k++)
                    if (a[k] > m) m = a[k];
                o[i] = m;
            }
            return o;
        }

        /// <summary>Moving average.</summary>
        private static float[] Smooth(float[] a, int radiusFrames)
        {
            var o = new float[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                int s = Math.Max(0, i - radiusFrames), e = Math.Min(a.Length - 1, i + radiusFrames);
                float sum = 0;
                for (int k = s; k <= e; k++) sum += a[k];
                o[i] = sum / (e - s + 1);
            }
            return o;
        }

        // ---------------------------------------------------------------------
        // Physics
        // ---------------------------------------------------------------------

        /// <summary>
        /// Drops a ball every DropIntervalMs and rolls it downhill. A ball is kept only if it
        /// came to rest (flat ground, or it crossed the bottom of a trough) within MaxRollMs
        /// AND moved at least MinDistanceTraveledMs. Returns the resting frame of every kept ball.
        /// </summary>
        private List<int> SimulateBallRolls(float[] terrain, float vocalFloor)
        {
            int n = terrain.Length;
            int radiusSamples = Math.Max(1, MsToFrames(BallRadiusMs));
            int dropStep = Math.Max(1, MsToFrames(DropIntervalMs));
            int minTravelSamples = Math.Max(1, MsToFrames(MinDistanceTraveledMs));
            int maxSteps = Math.Max(1, MsToFrames(MaxRollMs));

            var validLandings = new List<int>();
            r_rolls.Clear();

            for (int dropIdx = radiusSamples; dropIdx < n - radiusSamples; dropIdx += dropStep)
            {
                // Only drop balls where there is something above the noise floor
                if (terrain[dropIdx] < vocalFloor * DropFloorFactor)
                    continue;

                int cur = dropIdx;
                int lastDir = 0;
                bool settled = false;

                for (int step = 0; step < maxSteps; step++)
                {
                    // Ran off the usable range: not a resting place
                    if (cur <= radiusSamples || cur >= n - radiusSamples - 1)
                        break;

                    // Average height under the left and right halves of the ball
                    float leftHeight = 0f, rightHeight = 0f;
                    for (int k = 1; k <= radiusSamples; k++)
                    {
                        leftHeight += terrain[cur - k];
                        rightHeight += terrain[cur + k];
                    }
                    leftHeight /= radiusSamples;
                    rightHeight /= radiusSamples;

                    float slope = rightHeight - leftHeight;

                    // Flat ground: the ball stops here
                    if (Math.Abs(slope) < 1e-6f)
                    {
                        settled = true;
                        break;
                    }

                    int dir = slope > 0 ? -1 : 1; // slide downhill

                    // Direction flipped: the ball crossed the bottom of a trough, it rests here
                    // (no snapping to the lowest sample)
                    if (lastDir != 0 && dir != lastDir)
                    {
                        settled = true;
                        break;
                    }

                    lastDir = dir;
                    cur += dir;
                }

                bool kept = settled && Math.Abs(cur - dropIdx) >= minTravelSamples;
                r_rolls.Add((dropIdx, cur, kept));
                if (kept)
                    validLandings.Add(cur);
            }

            return validLandings;
        }

        /// <summary>
        /// Two balls cannot rest overlapping, so landings closer than one radius are the same
        /// resting place. No count threshold, no ranking, no refinement.
        /// </summary>
        private static List<int> ResolveRestingPlaces(List<int> landings, int radiusSamples)
        {
            var result = new List<int>();
            if (landings.Count == 0)
                return result;

            landings.Sort();
            int i = 0;
            while (i < landings.Count)
            {
                int start = i;
                while (i + 1 < landings.Count && landings[i + 1] - landings[i] < radiusSamples)
                    i++;
                result.Add(landings[(start + i) / 2]); // median landing of the group
                i++;
            }
            return result;
        }

        // ---------------------------------------------------------------------
        // Islands and segments
        // ---------------------------------------------------------------------

        private void BuildIslandsAndSegments(
            float[] terrain,
            List<int> valleyBreaks,
            float silenceFloor,
            out List<SpeechIsland> islands,
            out List<SpeechSegment> allSegments)
        {
            islands = new List<SpeechIsland>();
            allSegments = new List<SpeechSegment>();

            int n = terrain.Length;
            int minInterIslandFrames = MsToFrames((float)MinInterIslandSilence.TotalMilliseconds);

            // 1. Continuous spans above the silence floor
            var vocalSpans = new List<(int start, int end)>();
            int cur = 0;
            while (cur < n)
            {
                if (terrain[cur] > silenceFloor)
                {
                    int s = cur;
                    while (cur + 1 < n && terrain[cur + 1] > silenceFloor)
                        cur++;
                    vocalSpans.Add((s, cur));
                }
                cur++;
            }

            if (vocalSpans.Count == 0)
                return;

            // 2. Merge spans separated by less than MinInterIslandSilence into islands
            var mergedIslands = new List<(int start, int end)>();
            int islandStart = vocalSpans[0].start;
            int islandEnd = vocalSpans[0].end;

            for (int v = 1; v < vocalSpans.Count; v++)
            {
                if (vocalSpans[v].start - islandEnd < minInterIslandFrames)
                {
                    islandEnd = vocalSpans[v].end;
                }
                else
                {
                    mergedIslands.Add((islandStart, islandEnd));
                    islandStart = vocalSpans[v].start;
                    islandEnd = vocalSpans[v].end;
                }
            }
            mergedIslands.Add((islandStart, islandEnd));

            // 3. Every resting place inside an island is a cut. Nothing is filtered.
            for (int islIdx = 0; islIdx < mergedIslands.Count; islIdx++)
            {
                var (iStart, iEnd) = mergedIslands[islIdx];

                var filteredBreaks = valleyBreaks
                    .Where(b => b > iStart && b < iEnd)
                    .OrderBy(b => b)
                    .ToList();

                TimeSpan islandStartTime = FrameToTime(iStart);
                TimeSpan islandEndTime = FrameToTime(iEnd + 1);

                var breakTimes = filteredBreaks.Select(FrameToTime).ToList();
                var islandSegments = new List<SpeechSegment>();

                TimeSpan segStart = islandStartTime;
                foreach (TimeSpan segEnd in breakTimes)
                {
                    var seg = new SpeechSegment(allSegments.Count, islIdx, islandSegments.Count, segStart, segEnd, PeakBetween(terrain, segStart, segEnd));
                    islandSegments.Add(seg);
                    allSegments.Add(seg);
                    segStart = segEnd;
                }

                var lastSeg = new SpeechSegment(allSegments.Count, islIdx, islandSegments.Count, segStart, islandEndTime, PeakBetween(terrain, segStart, islandEndTime));
                islandSegments.Add(lastSeg);
                allSegments.Add(lastSeg);

                var island = new SpeechIsland(islIdx, islandStartTime, islandEndTime, breakTimes, islandSegments);
                foreach (var seg in islandSegments)
                    seg.Island = island;

                islands.Add(island);
            }
        }

        /// <summary>Highest terrain value in [from, to[.</summary>
        private float PeakBetween(float[] terrain, TimeSpan from, TimeSpan to)
        {
            if (terrain == null || terrain.Length == 0)
                return 0f;

            int a = Math.Max(0, TimeToFrame(from));
            int b = Math.Min(terrain.Length - 1, Math.Max(a, TimeToFrame(to) - 1));
            float peak = 0f;
            for (int k = a; k <= b; k++)
                if (terrain[k] > peak) peak = terrain[k];
            return peak;
        }

        /// <summary>Highest terrain value between two times (0 when the range is empty). Call after Compute().</summary>
        public float GetPeakLevel(TimeSpan from, TimeSpan to)
        {
            if (to <= from)
                return 0f;
            return PeakBetween(r_terrain, from, to);
        }

        private static void CalibrateFloors(float[] terrain, out float silenceFloor, out float vocalFloor)
        {
            float[] sorted = (float[])terrain.Clone();
            Array.Sort(sorted);

            float baseline = sorted[(int)(sorted.Length * 0.20f)];
            float peak = sorted[(int)(sorted.Length * 0.95f)];
            float range = Math.Max(0.001f, peak - baseline);

            silenceFloor = baseline + range * 0.04f;
            vocalFloor = baseline + range * 0.12f;
        }

        // ---------------------------------------------------------------------
        // Navigation: "jump mountains, stop at a dip"
        // ---------------------------------------------------------------------
        //
        // A "mountain" is a SpeechSegment: the stretch between two consecutive resting places
        // (or between an island edge and its first/last resting place). Silence is never a mountain.
        //
        // A START boundary only ever lands on a mountain start, an END boundary on a mountain end.
        //
        //   Outward : move a clip boundary OUTWARD (clip gets longer)
        //   Inward  : move a clip boundary INWARD  (clip gets shorter)
        //
        // FindDip walks the dips in the direction of the move and returns the first one that is
        // at least MinJump away from 'from', or (StopAtSpeechEdge) the first speech edge met,
        // whichever comes first. A speech edge is a dip with a significant mountain on one side
        // and an insignificant one (or silence) on the other side, so crossing a stretch of tiny
        // noise mountains is done in one jump and never steps over the first/last real syllable.

        private List<TimeSpan> r_segStarts = new List<TimeSpan>();
        private List<TimeSpan> r_segEnds = new List<TimeSpan>();

        private void BuildNavigationIndex()
        {
            // Segments are built in time order, so both lists are sorted ascending.
            r_segStarts = Segments.Select(s => s.StartTime).ToList();
            r_segEnds = Segments.Select(s => s.EndTime).ToList();
        }

        private List<TimeSpan> DipList(BoundarySide side) => side == BoundarySide.Start ? r_segStarts : r_segEnds;

        /// <summary>True when moving that side in that direction moves the boundary later in time.</summary>
        private static bool MovesLater(BoundarySide side, MoveDirection dir)
            => (side == BoundarySide.Start) == (dir == MoveDirection.Inward);

        /// <summary>
        /// First dip for that side, moving in that direction from 'from', that satisfies the options
        /// (see the navigation comment above). Returns null when there is none.
        /// </summary>
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

        /// <summary>Every dip of that side within [from, to], in time order.</summary>
        public IEnumerable<TimeSpan> GetDips(BoundarySide side, TimeSpan from, TimeSpan to)
        {
            var list = DipList(side);
            for (int i = LastIndexLess(list, from) + 1; i < list.Count && list[i] <= to; i++)
                yield return list[i];
        }

        /// <summary>
        /// True when the dip has a significant mountain on one side and an insignificant
        /// mountain (peak below significantLevel) or silence on the other side.
        /// </summary>
        public bool IsSpeechEdge(TimeSpan dip, float significantLevel)
        {
            bool before = IsSignificant(SegmentEndingAt(dip), significantLevel);
            bool after = IsSignificant(SegmentStartingAt(dip), significantLevel);
            return before != after;
        }

        /// <summary>True when 'time' is in silence, or inside a mountain whose peak is below significantLevel.</summary>
        public bool IsInsignificant(TimeSpan time, float significantLevel)
        {
            return !IsSignificant(SegmentAt(time), significantLevel);
        }

        /// <summary>True when 'time' is not inside any mountain (silence, or outside every island).</summary>
        public bool IsSilent(TimeSpan time)
        {
            return SegmentAt(time) == null;
        }

        private static bool IsSignificant(SpeechSegment seg, float significantLevel)
            => seg != null && seg.PeakLevel >= significantLevel;

        /// <summary>Mountain containing 'time' (null in silence).</summary>
        private SpeechSegment SegmentAt(TimeSpan time)
        {
            int i = LastIndexLess(r_segStarts, time + TimeSpan.FromTicks(1)); // last start <= time
            return (i < 0 || time >= r_segEnds[i]) ? null : Segments[i];
        }

        private SpeechSegment SegmentStartingAt(TimeSpan time)
        {
            int i = LastIndexLess(r_segStarts, time + TimeSpan.FromTicks(1));
            return (i >= 0 && r_segStarts[i] == time) ? Segments[i] : null;
        }

        private SpeechSegment SegmentEndingAt(TimeSpan time)
        {
            int i = LastIndexLess(r_segEnds, time + TimeSpan.FromTicks(1));
            return (i >= 0 && r_segEnds[i] == time) ? Segments[i] : null;
        }

        /// <summary>Index of the first element greater than v (list.Count if none).</summary>
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

        /// <summary>Index of the last element smaller than v (-1 if none).</summary>
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

        public IEnumerable<Subtitle> ToSegmentSubtitles()
        {
            var oneMs = TimeSpan.FromMilliseconds(1);
            foreach (var island in Islands)
            {
                string label = island.DisplayLetter.ToString();
                for (int i = 0; i < island.Segments.Count; i++)
                {
                    var seg = island.Segments[i];
                    TimeSpan endTime = (i < island.Segments.Count - 1 && seg.Duration > oneMs)
                        ? seg.EndTime - oneMs
                        : seg.EndTime;

                    yield return new Subtitle(seg.StartTime, endTime, new[] { label });
                }
            }
        }

        /// <summary>
        /// Renders the terrain the balls actually see, with the landing histogram underneath.
        /// Call after Compute().
        ///   - orange line : drops happen only above this
        ///   - grey line   : silence floor
        ///   - green bars  : kept landings,  grey bars : discarded landings
        ///   - red lines   : final cuts,     blue lines : island bounds
        ///   - white box   : ball diameter at this scale
        /// </summary>
        public void SaveDebugImage(string path, TimeSpan start, TimeSpan duration,
                                   float msPerPixel = 3f, int terrainHeight = 400, int histHeight = 120)
        {
            if (r_terrain == null || r_terrain.Length == 0)
                return;

            int w = (int)(duration.TotalMilliseconds / msPerPixel);
            if (w <= 0)
                return;

            int f0 = Math.Max(0, TimeToFrame(start));
            double startMs = f0 * (double)MsPerFrame;
            int totalH = terrainHeight + histHeight + 20;

            int ToX(int frame) => (int)((frame * (double)MsPerFrame - startMs) / msPerPixel);
            int framesPerPx = Math.Max(1, (int)Math.Round(msPerPixel / MsPerFrame));

            // Terrain value per pixel column (max in the column)
            var col = new float[w];
            float peak = 0.0001f;
            for (int x = 0; x < w; x++)
            {
                int a = f0 + (int)(x * msPerPixel / MsPerFrame);
                int b = Math.Min(r_terrain.Length - 1, a + framesPerPx);
                float m = 0;
                for (int k = a; k <= b && k < r_terrain.Length; k++)
                    m = Math.Max(m, r_terrain[k]);
                col[x] = m;
                peak = Math.Max(peak, m);
            }

            // Landing counts per pixel column
            var keptHist = new int[w];
            var discardedHist = new int[w];
            foreach (var r in r_rolls)
            {
                int x = ToX(r.land);
                if (x < 0 || x >= w) continue;
                if (r.kept) keptHist[x]++; else discardedHist[x]++;
            }
            int maxHist = Math.Max(1, Math.Max(keptHist.Max(), discardedHist.Max()));

            CalibrateFloors(r_terrain, out float silenceFloor, out float vocalFloor);

            using (var bmp = new Bitmap(w, totalH))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(30, 30, 30));

                // Terrain (filled)
                using (var pen = new Pen(Color.FromArgb(170, 230, 60)))
                    for (int x = 0; x < w; x++)
                    {
                        int h = (int)(col[x] / peak * (terrainHeight - 10));
                        g.DrawLine(pen, x, terrainHeight - h, x, terrainHeight);
                    }

                // Floors
                int ySil = terrainHeight - (int)(silenceFloor / peak * (terrainHeight - 10));
                int yVoc = terrainHeight - (int)(vocalFloor * DropFloorFactor / peak * (terrainHeight - 10));
                g.DrawLine(Pens.Gray, 0, ySil, w, ySil);
                g.DrawLine(Pens.Orange, 0, yVoc, w, yVoc);

                // Landing histogram
                int y0 = terrainHeight + 10;
                for (int x = 0; x < w; x++)
                {
                    int hk = keptHist[x] * histHeight / maxHist;
                    int hd = discardedHist[x] * histHeight / maxHist;
                    g.DrawLine(Pens.DimGray, x, y0 + histHeight, x, y0 + histHeight - hd);
                    g.DrawLine(Pens.LimeGreen, x, y0 + histHeight, x, y0 + histHeight - hk);
                }

                // Final cuts (red)
                foreach (int frame in r_breaks)
                {
                    int x = ToX(frame);
                    if (x >= 0 && x < w) g.DrawLine(Pens.Red, x, 0, x, totalH);
                }

                // Island bounds (blue)
                foreach (var isl in Islands)
                {
                    int xs = (int)((isl.StartTime.TotalMilliseconds - startMs) / msPerPixel);
                    int xe = (int)((isl.EndTime.TotalMilliseconds - startMs) / msPerPixel);
                    if (xs >= 0 && xs < w) g.DrawLine(Pens.DeepSkyBlue, xs, 0, xs, totalH);
                    if (xe >= 0 && xe < w) g.DrawLine(Pens.DeepSkyBlue, xe, 0, xe, totalH);
                }

                // Ball diameter reference
                int ballPx = (int)(2 * BallRadiusMs / msPerPixel);
                g.DrawRectangle(Pens.White, 4, 4, ballPx, 6);
                g.DrawString("ball", SystemFonts.DefaultFont, Brushes.White, 6 + ballPx, 0);

                // Time ticks every 500 ms
                for (double ms = Math.Ceiling(startMs / 500) * 500; ms < startMs + duration.TotalMilliseconds; ms += 500)
                {
                    int x = (int)((ms - startMs) / msPerPixel);
                    g.DrawLine(Pens.White, x, totalH - 10, x, totalH);
                    g.DrawString(TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss\.fff"),
                                 SystemFonts.DefaultFont, Brushes.White, x + 2, totalH - 14);
                }

                bmp.Save(path, ImageFormat.Jpeg);
            }
        }
    }
}
