using FunscriptToolbox.Core;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Inputs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FunscriptToolbox.SubtitlesVerbs.Processors.Refiner
{
    /// <summary>
    /// Finds every place where speech (or a syllable of speech) could start or end, and says what each piece of audio is.
    ///
    /// Nothing is decided by comparing a mountain to the silence with a fixed ratio: every level decision comes from the
    /// statistics of the noise of the file itself, and "is it speech" comes from voicing (periodicity), not from loudness.
    ///
    /// 1. TERRAIN      : peak envelope per ms (pitch teeth removed, lightly smoothed), in dB.
    /// 2. VOICING      : normalized autocorrelation peak (pitch 70..800 Hz) every 5 ms. ~0.2 on noise / breath, ~0.9 on a vowel.
    /// 3. NOISE FLOOR  : low percentile of every block that has NO voicing, then the lowest of those near the moment (+/- 2 s).
    ///                   Measured near the moment because the source is dynamically normalized (loudnorm): its noise goes up and down.
    /// 4. NOISE MODEL  : how far the noise frames go above the floor (their distribution, measured on the file). A frame is
    ///                   "above the noise" when it is louder than almost every noise frame (NoiseCeilingPercentile), and stays above
    ///                   until it falls back inside the bulk of the noise (NoiseReturnPercentile). Those crossings are the edges.
    /// 5. CUTS         : inside a blob, a valley is a cut when it "reaches down" (ValleyReach, same meaning as V1).
    /// 6. KIND         : every piece is Speech (it has a voiced core), Breath (above the noise, but never voiced: breath, rustle,
    ///                   exhale...) or Click (a short unvoiced spike).
    ///
    /// Two different questions are answered:
    /// For the refiner (Segments, dips, IsSilent, padding...), Breath and Click are SILENCE: only Speech pieces are not silence,
    /// and the dips are the edges of the Speech pieces only. The detail (Breath, Click) is kept in DetailedSegments, for the TERRAIN-MAP.
    ///
    /// The segments are contiguous and cover the whole audio.
    /// </summary>
    public class AudioTerrainMapping
    {
        // ---------------------------------------------------------------------
        // Knobs
        // ---------------------------------------------------------------------

        /// <summary>A valley is a cut if it comes within this fraction of the way from the floor to the lower neighbouring peak (linear amplitude). Lower = fewer cuts.</summary>
        public float ValleyReach { get; set; } = 0.25f;

        /// <summary>A frame starts a blob when it is louder than this fraction of the noise frames (e.g. 0.995 = louder than 99.5% of the noise).</summary>
        public float NoiseCeilingPercentile { get; set; } = 0.995f;

        /// <summary>A blob ends when the terrain falls back below this fraction of the noise frames (hysteresis, so the edge is where the sound re-enters the noise).</summary>
        public float NoiseReturnPercentile { get; set; } = 0.90f;

        /// <summary>Voicing (0..1) above which a frame is voiced. The measure is ~0.2 on noise and ~0.9 on a vowel, whatever the level.</summary>
        public float VoicedThreshold { get; set; } = 0.6f;

        /// <summary>A piece is Speech when it has at least this much consecutive voicing.</summary>
        public TimeSpan MinVoicedCore { get; set; } = TimeSpan.FromMilliseconds(20);

        /// <summary>Unvoiced pieces (Breath / Click) touching a Speech piece, up to this much in total on each side, become part of the speech (unvoiced consonants).</summary>
        public TimeSpan MaxAttachedUnvoiced { get; set; } = TimeSpan.FromMilliseconds(150);

        /// <summary>A Breath (not a Click) becomes Speech (whisper) when it is within this many dB of the peak of a Speech piece next to it.
        /// A faint breath, or the exhale after a loud and clear voice, stays below that and is not promoted.</summary>
        public float WhisperRelativeDb { get; set; } = 6f;

        /// <summary>A Breath is only promoted if a Speech piece (or an already promoted Breath) is at most this far from it. A Breath on an island is never promoted.</summary>
        /// <remarks>25 ms (was 300 ms): with a wider gap, the breath after the last word became "speech" and the snapped End was extended over it
        /// (HNVR-141: both bounds good +3 to +9 points, see GeminiDecisionProbes.md, "Terrain: why the snapped Ends are too wide").</remarks>
        public TimeSpan MaxWhisperGap { get; set; } = TimeSpan.FromMilliseconds(25);

        /// <summary>A run of at least this many loud Breath pieces, close to each other, is a whispered phrase (even with no Speech around): a real breath is a single bump, a whisper is a series of syllables.</summary>
        public int WhisperMinPieces { get; set; } = 2;

        /// <summary>Largest gap between two pieces of the same whispered phrase.</summary>
        public TimeSpan WhisperClusterGap { get; set; } = TimeSpan.FromMilliseconds(150);

        /// <summary>A Breath piece must be at least this far above the noise floor (dB) to be part of a whispered phrase.</summary>
        public float WhisperClusterMinDb { get; set; } = 20f;

        /// <summary>A Speech piece shorter than this that touches another Speech piece is merged into it (no dip between them).</summary>
        public TimeSpan MinSpeechPiece { get; set; } = TimeSpan.FromMilliseconds(50);

        /// <summary>An unvoiced piece shorter than this is a Click, longer is a Breath.</summary>
        public TimeSpan MaxClickDuration { get; set; } = TimeSpan.FromMilliseconds(50);

        // Fixed internals
        private const int MsPerFrame = 1;
        private const float MaxFilterMs = 6f;              // removes pitch teeth
        private const float SmoothMs = 3f;
        private const float DigitalSilenceDb = -90f;       // below this, there is no signal at all (muted / gated)
        private const int FloorBlockMs = 100;
        private const float FloorPercentile = 0.10f;
        private const int FloorNeighbourMs = 500;          // the floor is the lowest unvoiced block within +/- this (short: the noise of a loudnorm'ed source steps up and down within a second)
        private const int FloorFallbackNeighbourMs = 15000;// ...or within +/- this when there is no unvoiced block nearby
        private const float NoiseBlockToleranceDb = 1.5f;  // a block is a "noise block" (used to learn the noise model) when its low percentile is this close to the floor
        private const int VoicingHopMs = 5;
        private const int VoicingWindowMs = 25;
        private const int VoicingMinHz = 70;
        private const int VoicingMaxHz = 800;
        private const float ValleyWindowMs = 15f;          // local-minimum window
        private const int MinBlobMs = 10;                  // a blob shorter than this is ignored (a flicker of the noise)

        // ---------------------------------------------------------------------
        // Result type
        // ---------------------------------------------------------------------

        public enum SegmentKind
        {
            /// <summary>Nothing above the noise.</summary>
            Silence,
            /// <summary>Above the noise, with a voiced core.</summary>
            Speech,
            /// <summary>Above the noise for a while, never voiced (breath, exhale, whisper, rustle...).</summary>
            Breath,
            /// <summary>Above the noise for a very short time, never voiced (click, lip smack, tap...).</summary>
            Click
        }

        public class SpeechSegment
        {
            public int GlobalIndex { get; }
            public TimeSpan StartTime { get; }
            public TimeSpan EndTime { get; }
            public TimeSpan Duration => EndTime - StartTime;
            public SegmentKind Kind { get; }

            /// <summary>True only for a stretch with nothing above the noise (a Breath or a Click is NOT silence: it is not speech either).</summary>
            public bool IsSilence => Kind == SegmentKind.Silence;

            /// <summary>True for a piece of suspected speech.</summary>
            public bool IsSpeech => Kind == SegmentKind.Speech;

            /// <summary>Highest terrain value inside the segment (linear, 0 = digital silence).</summary>
            public float PeakLevel { get; }

            /// <summary>Highest terrain inside the segment, in dB above the local noise floor.</summary>
            public float PeakAboveFloorDb { get; }

            /// <summary>Longest run of voiced frames inside the segment.</summary>
            public TimeSpan LongestVoicedRun { get; }

            internal SpeechSegment(int globalIndex, TimeSpan startTime, TimeSpan endTime, SegmentKind kind, float peakLevel, float peakAboveFloorDb, TimeSpan longestVoicedRun)
            {
                GlobalIndex = globalIndex;
                StartTime = startTime;
                EndTime = endTime;
                Kind = kind;
                PeakLevel = peakLevel;
                PeakAboveFloorDb = peakAboveFloorDb;
                LongestVoicedRun = longestVoicedRun;
            }
        }

        // ---------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------

        private readonly PcmAudio r_fullAudio;

        /// <summary>What the refiner uses: only Speech and Silence (Breath and Click are silence, merged with the silence around them).</summary>
        public List<SpeechSegment> Segments { get; private set; } = new List<SpeechSegment>();

        /// <summary>Every piece with its kind (Speech, Breath, Click, Silence), for the TERRAIN-MAP.</summary>
        public List<SpeechSegment> DetailedSegments { get; private set; } = new List<SpeechSegment>();

        /// <summary>The noise model learned from the file (dB above the floor): where the bulk of the noise ends, and where almost all of it ends.</summary>
        public float NoiseReturnDb { get; private set; }
        public float NoiseCeilingDb { get; private set; }

        private float[] r_terrain = new float[0];   // linear
        private float[] r_terrainDb = new float[0];
        private float[] r_floorDb = new float[0];
        private float[] r_voicing = new float[0];   // per ms (copied from the 5 ms frames)

        public AudioTerrainMapping(PcmAudio fullAudio)
        {
            r_fullAudio = fullAudio ?? throw new ArgumentNullException(nameof(fullAudio));
        }

        public TimeSpan FrameToTime(int frame) => TimeSpan.FromMilliseconds(frame * MsPerFrame);
        public int TimeToFrame(TimeSpan time) => (int)Math.Round(time.TotalMilliseconds / MsPerFrame);
        private static int Frames(float ms) => Math.Max(1, (int)Math.Round(ms / MsPerFrame));

        // ---------------------------------------------------------------------
        // Pipeline
        // ---------------------------------------------------------------------

        public void Compute()
        {
            Segments = new List<SpeechSegment>();
            DetailedSegments = new List<SpeechSegment>();

            var samples = r_fullAudio.ToFloat();
            float[] terrain = r_fullAudio.ToPeak(MsPerFrame);
            int n = terrain.Length;
            if (n == 0)
            {
                r_terrain = terrain;
                r_terrainDb = r_floorDb = r_voicing = new float[0];
                BuildNavigationIndex();
                return;
            }

            // 1. Terrain
            int mr = Frames(MaxFilterMs);
            terrain = SlidingExtreme(terrain, mr, mr, true);
            terrain = Smooth(terrain, Frames(SmoothMs));
            r_terrain = terrain;
            r_terrainDb = terrain.Select(ToDb).ToArray();

            // 2. Voicing, only where something could be above the noise (a loose floor is enough to know where not to look)
            var looseFloor = ComputeFloor(r_terrainDb, null, FloorFallbackNeighbourMs);
            r_voicing = ComputeVoicing(samples, r_fullAudio.SamplingRate, i => r_terrainDb[i] > looseFloor[i] + 3f);

            // 3-4. Floor (unvoiced blocks only, near the moment) and noise model
            r_floorDb = ComputeFloor(r_terrainDb, r_voicing, FloorNeighbourMs);
            LearnNoiseModel();

            // 5. Blobs, then cuts inside the blobs
            var blobs = FindBlobs();
            var pieces = new List<(int start, int end)>();
            foreach (var (start, end) in blobs)
            {
                int s = start;
                foreach (int c in SplitBlob(start, end))
                {
                    pieces.Add((s, c));
                    s = c;
                }
                pieces.Add((s, end));
            }

            // 6. Kinds, and the silences between
            BuildSegments(pieces);
            BuildNavigationIndex();
        }

        private static float ToDb(float v) => v <= 0f ? -120f : Math.Max(-120f, 20f * (float)Math.Log10(v));

        // ---------------------------------------------------------------------
        // 2. Voicing
        // ---------------------------------------------------------------------

        /// <summary>
        /// Normalized autocorrelation peak (lags of a pitch between VoicingMinHz and VoicingMaxHz) on VoicingWindowMs windows,
        /// every VoicingHopMs, computed at 8 kHz (the source is low-passed anyway). Returned per ms. 0 where 'isWorthIt' is false.
        /// </summary>
        private static float[] ComputeVoicing(float[] samples, int samplingRate, Func<int, bool> isWorthIt)
        {
            // Decimate to ~8 kHz (average of 'dec' samples)
            int dec = Math.Max(1, samplingRate / 8000);
            int rate = samplingRate / dec;
            var x = new float[samples.Length / dec];
            for (int i = 0; i < x.Length; i++)
            {
                float sum = 0f;
                for (int k = 0; k < dec; k++) sum += samples[i * dec + k];
                x[i] = sum / dec;
            }

            int nMs = (int)((long)samples.Length * 1000 / samplingRate);
            int win = rate * VoicingWindowMs / 1000;
            int minLag = rate / VoicingMaxHz;
            int maxLag = rate / VoicingMinHz;
            int hopFrames = (nMs + VoicingHopMs - 1) / VoicingHopMs;
            var perHop = new float[hopFrames];

            Parallel.For(0, hopFrames, h =>
            {
                int centreMs = h * VoicingHopMs + VoicingHopMs / 2;
                if (centreMs >= nMs || !isWorthIt(centreMs))
                    return;
                int o = (int)((long)centreMs * rate / 1000) - win / 2;
                if (o < 0 || o + win + maxLag >= x.Length)
                    return;

                double e0 = 0;
                for (int k = 0; k < win; k++) e0 += x[o + k] * x[o + k];
                if (e0 <= 1e-12)
                    return;

                // Energy of the lagged window, updated incrementally
                double e1 = 0;
                for (int k = 0; k < win; k++) e1 += x[o + minLag + k] * x[o + minLag + k];

                double best = 0;
                for (int lag = minLag; lag <= maxLag; lag++)
                {
                    if (lag > minLag)
                    {
                        float outV = x[o + lag - 1], inV = x[o + lag - 1 + win];
                        e1 += inV * inV - outV * outV;
                    }
                    double c = 0;
                    for (int k = 0; k < win; k++) c += x[o + k] * x[o + k + lag];
                    double r = c / Math.Sqrt(e0 * Math.Max(1e-12, e1));
                    if (r > best) best = r;
                }
                perHop[h] = (float)best;
            });

            var perMs = new float[nMs];
            for (int i = 0; i < nMs; i++)
                perMs[i] = perHop[Math.Min(hopFrames - 1, i / VoicingHopMs)];
            return perMs;
        }

        // ---------------------------------------------------------------------
        // 3. Noise floor
        // ---------------------------------------------------------------------

        /// <summary>
        /// Low percentile of every block (in dB). A block that is digital silence, or that has a voiced frame (when 'voicing' is given),
        /// cannot be noise. The floor is the lowest noise block within +/- neighbourMs (or within FloorFallbackNeighbourMs when there is
        /// none), then averaged over the same distance so it moves smoothly.
        /// </summary>
        private float[] ComputeFloor(float[] db, float[] voicing, int neighbourMs)
        {
            int n = db.Length;
            int block = Frames(FloorBlockMs);
            int nb = (n + block - 1) / block;

            var perBlock = new float[nb];
            var slice = new float[block];
            for (int b = 0; b < nb; b++)
            {
                int s = b * block;
                int len = Math.Min(block, n - s);
                bool voiced = false;
                for (int k = 0; k < len; k++)
                {
                    slice[k] = db[s + k];
                    if (voicing != null && voicing[s + k] >= VoicedThreshold)
                        voiced = true;
                }
                Array.Sort(slice, 0, len);
                float val = slice[Math.Min(len - 1, (int)(len * FloorPercentile))];
                perBlock[b] = (val <= DigitalSilenceDb || voiced) ? float.PositiveInfinity : val;
            }

            int r = Math.Max(1, neighbourMs / block);
            var lowest = SlidingExtreme(perBlock, r, r, false);
            if (lowest.Any(float.IsPositiveInfinity))
            {
                int rFallback = Math.Max(r, FloorFallbackNeighbourMs / block);
                var wide = SlidingExtreme(perBlock, rFallback, rFallback, false);
                float global = perBlock.Where(v => !float.IsInfinity(v)).DefaultIfEmpty(DigitalSilenceDb).Min();
                for (int b = 0; b < nb; b++)
                    if (float.IsPositiveInfinity(lowest[b]))
                        lowest[b] = float.IsPositiveInfinity(wide[b]) ? global : wide[b];
            }
            lowest = Smooth(lowest, r);

            var floor = new float[n];
            for (int i = 0; i < n; i++)
            {
                float pos = (i - block / 2f) / block;
                int i0 = Math.Max(0, Math.Min(nb - 1, (int)Math.Floor(pos)));
                int i1 = Math.Min(nb - 1, i0 + 1);
                float f = Math.Max(0f, Math.Min(1f, pos - i0));
                floor[i] = lowest[i0] + (lowest[i1] - lowest[i0]) * f;
            }
            return floor;
        }

        // ---------------------------------------------------------------------
        // 4. Noise model
        // ---------------------------------------------------------------------

        /// <summary>
        /// The noise blocks are the unvoiced blocks whose low percentile is (about) the floor itself: they are the noise.
        /// Their frames tell how far the noise goes above the floor (dB). Those distances become the thresholds.
        /// </summary>
        private void LearnNoiseModel()
        {
            int n = r_terrainDb.Length;
            int block = Frames(FloorBlockMs);
            var above = new List<float>(n / 4);
            var slice = new float[block];
            for (int s = 0; s + block <= n; s += block)
            {
                bool usable = true;
                for (int k = 0; k < block && usable; k++)
                {
                    slice[k] = r_terrainDb[s + k];
                    if (r_voicing[s + k] >= VoicedThreshold || slice[k] <= DigitalSilenceDb)
                        usable = false;
                }
                if (!usable)
                    continue;
                Array.Sort(slice);
                float low = slice[(int)(block * FloorPercentile)];
                if (low - r_floorDb[s + block / 2] > NoiseBlockToleranceDb)
                    continue;
                for (int k = 0; k < block; k++)
                    above.Add(r_terrainDb[s + k] - r_floorDb[s + k]);
            }

            if (above.Count < 100)
                throw new InvalidOperationException($"AudioTerrainMappingV2: not enough noise to learn the noise model ({above.Count} frames).");

            // The noise blocks still hold the odd breath / click, and a high percentile is very sensitive to that. So the tail of the
            // noise is extrapolated from its bulk (median and p50 - p10), as for a bell curve: a few loud frames cannot move it.
            var sorted = above.ToArray();
            Array.Sort(sorted);
            float p10 = sorted[(int)(sorted.Length * 0.10f)];
            float p50 = sorted[sorted.Length / 2];
            float sigma = Math.Max(0.1f, (p50 - p10) / NormalQuantile(0.90f));
            NoiseReturnDb = p50 + sigma * NormalQuantile(NoiseReturnPercentile);
            NoiseCeilingDb = p50 + sigma * NormalQuantile(NoiseCeilingPercentile);
        }

        /// <summary>z such that P(Z &lt;= z) = p for a standard normal (Acklam's approximation, plenty for this).</summary>
        private static float NormalQuantile(float p)
        {
            double q, r;
            double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
            double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
            double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
            double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
            p = Math.Max(1e-6f, Math.Min(1 - 1e-6f, p));
            if (p < 0.02425)
            {
                q = Math.Sqrt(-2 * Math.Log(p));
                return (float)((((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1));
            }
            if (p > 1 - 0.02425)
            {
                q = Math.Sqrt(-2 * Math.Log(1 - p));
                return (float)-((((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1));
            }
            q = p - 0.5;
            r = q * q;
            return (float)((((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1));
        }

        // ---------------------------------------------------------------------
        // 5. Blobs and cuts
        // ---------------------------------------------------------------------

        /// <summary>Stretches above the noise: they start above NoiseCeilingDb and end where the terrain falls back below NoiseReturnDb.</summary>
        private List<(int start, int end)> FindBlobs()
        {
            int n = r_terrainDb.Length;
            var blobs = new List<(int start, int end)>();
            int minBlob = Frames(MinBlobMs);

            bool Above(int i, float db) => r_terrainDb[i] - r_floorDb[i] > db;

            int i0 = 0;
            while (i0 < n)
            {
                if (!Above(i0, NoiseCeilingDb))
                {
                    i0++;
                    continue;
                }

                // Back to where it left the noise, forward to where it re-enters it
                int s = i0;
                int prevEnd = blobs.Count > 0 ? blobs[blobs.Count - 1].end : 0;
                while (s - 1 >= prevEnd && Above(s - 1, NoiseReturnDb))
                    s--;
                int e = i0 + 1;
                while (e < n && Above(e, NoiseReturnDb))
                    e++;

                if (e - s >= minBlob)
                    blobs.Add((s, e));
                i0 = e;
            }
            return blobs;
        }

        /// <summary>Linear amplitude of the floor.</summary>
        private float FloorLin(int i) => (float)Math.Pow(10, r_floorDb[i] / 20.0);

        /// <summary>The cuts inside the blob [bs, be[ (sorted): local minima that reach down (V1's rule, against the new floor).</summary>
        private List<int> SplitBlob(int bs, int be)
        {
            var t = r_terrain;
            int w = Frames(ValleyWindowMs);

            var cand = new List<int>();
            for (int i = bs + 1; i < be - 1; i++)
            {
                bool isMin = true;
                for (int k = Math.Max(bs, i - w); k <= Math.Min(be - 1, i + w) && isMin; k++)
                    if (t[k] < t[i]) isMin = false;
                if (!isMin)
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

            // Mountains between the candidates; the valleys that do not reach down are removed, the shallowest first
            var starts = new List<int> { bs };
            var peaks = new List<float>();
            foreach (int c in cand) starts.Add(c);
            for (int k = 0; k < starts.Count; k++)
                peaks.Add(Peak(t, starts[k], k + 1 < starts.Count ? starts[k + 1] : be));

            while (starts.Count > 1)
            {
                int worst = -1;
                float worstRatio = ValleyReach;
                for (int k = 1; k < starts.Count; k++)
                {
                    int c = starts[k];
                    float floor = FloorLin(c);
                    float denom = Math.Min(peaks[k - 1], peaks[k]) - floor;
                    float ratio = denom <= 1e-9f ? 1f : Math.Max(0f, Math.Min(1f, (t[c] - floor) / denom));
                    if (ratio > worstRatio)
                    {
                        worstRatio = ratio;
                        worst = k;
                    }
                }
                if (worst < 0)
                    break;
                peaks[worst - 1] = Math.Max(peaks[worst - 1], peaks[worst]);
                peaks.RemoveAt(worst);
                starts.RemoveAt(worst);
            }

            return starts.Skip(1).ToList();
        }

        private static float Peak(float[] t, int from, int to)
        {
            float peak = 0f;
            for (int k = Math.Max(0, from); k < Math.Min(t.Length, to); k++)
                if (t[k] > peak) peak = t[k];
            return peak;
        }

        // ---------------------------------------------------------------------
        // 6. Segments
        // ---------------------------------------------------------------------

        private void BuildSegments(List<(int start, int end)> pieces)
        {
            int n = r_terrain.Length;
            int minCore = Frames((float)MinVoicedCore.TotalMilliseconds);
            int maxClick = Frames((float)MaxClickDuration.TotalMilliseconds);
            var result = new List<SpeechSegment>(pieces.Count * 2 + 1);

            void Add(int from, int to, SegmentKind? forcedKind)
            {
                if (to <= from)
                    return;
                float peak = 0f, peakDb = float.NegativeInfinity;
                int run = 0, bestRun = 0;
                for (int k = from; k < to; k++)
                {
                    peak = Math.Max(peak, r_terrain[k]);
                    peakDb = Math.Max(peakDb, r_terrainDb[k] - r_floorDb[k]);
                    run = r_voicing[k] >= VoicedThreshold ? run + 1 : 0;
                    bestRun = Math.Max(bestRun, run);
                }
                var kind = forcedKind
                    ?? (bestRun >= minCore ? SegmentKind.Speech
                    : to - from <= maxClick ? SegmentKind.Click
                    : SegmentKind.Breath);
                result.Add(new SpeechSegment(result.Count, FrameToTime(from), FrameToTime(to), kind, peak, peakDb, FrameToTime(bestRun)));
            }

            int pos = 0;
            foreach (var (start, end) in pieces)
            {
                Add(pos, start, SegmentKind.Silence);
                Add(start, end, null);
                pos = end;
            }
            Add(pos, n, SegmentKind.Silence);

            result = AttachUnvoicedToSpeech(result);
            result = PromoteWhispers(result);
            result = MergeShortSpeech(result);
            DetailedSegments = result;

            // What the refiner sees: Breath and Click are silence (merged with the silence around them)
            var merged = new List<SpeechSegment>(result.Count);
            foreach (var seg in result)
            {
                if (!seg.IsSpeech && merged.Count > 0 && !merged[merged.Count - 1].IsSpeech)
                {
                    var prev = merged[merged.Count - 1];
                    merged[merged.Count - 1] = new SpeechSegment(prev.GlobalIndex, prev.StartTime, seg.EndTime, SegmentKind.Silence,
                        Math.Max(prev.PeakLevel, seg.PeakLevel), Math.Max(prev.PeakAboveFloorDb, seg.PeakAboveFloorDb), TimeSpan.Zero);
                }
                else
                {
                    merged.Add(new SpeechSegment(merged.Count, seg.StartTime, seg.EndTime, seg.IsSpeech ? SegmentKind.Speech : SegmentKind.Silence,
                        seg.PeakLevel, seg.PeakAboveFloorDb, seg.LongestVoicedRun));
                }
            }
            Segments = merged;
        }

        /// <summary>
        /// An unvoiced piece (Breath or Click) that touches a Speech piece is most likely an unvoiced consonant (s, sh, h, ts...) or a
        /// devoiced vowel of the same word: it becomes Speech, so its outer edge is the speech edge. Pieces are taken one after the other
        /// outward, as long as the total stays within MaxAttachedUnvoiced of the voiced piece. Pieces with silence between are not touched.
        /// </summary>
        private List<SpeechSegment> AttachUnvoicedToSpeech(List<SpeechSegment> segs)
        {
            var attach = new bool[segs.Count];
            for (int i = 0; i < segs.Count; i++)
            {
                if (segs[i].Kind != SegmentKind.Speech || attach[i])
                    continue;
                foreach (int dir in new[] { -1, 1 })
                {
                    var total = TimeSpan.Zero;
                    for (int j = i + dir; j >= 0 && j < segs.Count; j += dir)
                    {
                        var k = segs[j].Kind;
                        if (k != SegmentKind.Breath && k != SegmentKind.Click)
                            break;
                        total += segs[j].Duration;
                        if (total > MaxAttachedUnvoiced)
                            break;
                        attach[j] = true;
                    }
                }
            }

            var result = new List<SpeechSegment>(segs.Count);
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                result.Add(new SpeechSegment(i, s.StartTime, s.EndTime, attach[i] ? SegmentKind.Speech : s.Kind, s.PeakLevel, s.PeakAboveFloorDb, s.LongestVoicedRun));
            }
            return result;
        }

        /// <summary>
        /// Whispered words have no voicing, so they look like a Breath. A Breath is promoted to Speech when a Speech piece is within
        /// MaxWhisperGap of it and the Breath is not much quieter than that piece (WhisperRelativeDb). Repeated, so a whisper made of
        /// several pieces is promoted from the speech outward. A Breath on an island (no Speech nearby), a faint breath, or the exhale
        /// after a loud voice stays a Breath (= silence).
        /// </summary>
        private List<SpeechSegment> PromoteWhispers(List<SpeechSegment> segs)
        {
            var list = new List<SpeechSegment>(segs);

            // Whispered phrases without any voiced speech around: a series of loud Breath pieces, close to each other
            var chain = new List<int>();
            void Flush()
            {
                if (chain.Count >= WhisperMinPieces)
                    foreach (int idx in chain)
                    {
                        var c = list[idx];
                        list[idx] = new SpeechSegment(c.GlobalIndex, c.StartTime, c.EndTime, SegmentKind.Speech, c.PeakLevel, c.PeakAboveFloorDb, c.LongestVoicedRun);
                    }
                chain.Clear();
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].IsSpeech)
                    Flush();
                else if (list[i].Kind == SegmentKind.Breath && list[i].PeakAboveFloorDb >= WhisperClusterMinDb)
                {
                    if (chain.Count > 0 && list[i].StartTime - list[chain[chain.Count - 1]].EndTime > WhisperClusterGap)
                        Flush();
                    chain.Add(i);
                }
            }
            Flush();

            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < list.Count; i++)
                {
                    var cur = list[i];
                    if (cur.Kind != SegmentKind.Breath)
                        continue;

                    bool promote = false;
                    foreach (int dir in new[] { -1, 1 })
                    {
                        var gap = TimeSpan.Zero;
                        for (int j = i + dir; j >= 0 && j < list.Count && !promote; j += dir)
                        {
                            if (list[j].IsSpeech)
                            {
                                promote = cur.PeakAboveFloorDb >= list[j].PeakAboveFloorDb - WhisperRelativeDb;
                                break;
                            }
                            gap += list[j].Duration;
                            if (gap > MaxWhisperGap)
                                break;
                        }
                        if (promote)
                            break;
                    }
                    if (promote)
                    {
                        list[i] = new SpeechSegment(cur.GlobalIndex, cur.StartTime, cur.EndTime, SegmentKind.Speech, cur.PeakLevel, cur.PeakAboveFloorDb, cur.LongestVoicedRun);
                        changed = true;
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// A Speech piece shorter than MinSpeechPiece that touches another Speech piece is not a syllable of its own: it is merged
        /// into the longer of its touching neighbours (so the cut between them is not offered as a dip).
        /// An isolated short piece (nothing touching) is left alone.
        /// </summary>
        private List<SpeechSegment> MergeShortSpeech(List<SpeechSegment> segs)
        {
            var list = new List<SpeechSegment>(segs);
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < list.Count; i++)
                {
                    var cur = list[i];
                    if (!cur.IsSpeech || cur.Duration >= MinSpeechPiece)
                        continue;

                    var prev = i > 0 && list[i - 1].IsSpeech && list[i - 1].EndTime == cur.StartTime ? list[i - 1] : null;
                    var next = i + 1 < list.Count && list[i + 1].IsSpeech && list[i + 1].StartTime == cur.EndTime ? list[i + 1] : null;
                    if (prev == null && next == null)
                        continue;

                    int into = (prev != null && (next == null || prev.Duration >= next.Duration)) ? i - 1 : i + 1;
                    var other = list[into];
                    var merged = new SpeechSegment(0,
                        TimeSpanExtensions.Min(cur.StartTime, other.StartTime), TimeSpanExtensions.Max(cur.EndTime, other.EndTime),
                        SegmentKind.Speech, Math.Max(cur.PeakLevel, other.PeakLevel), Math.Max(cur.PeakAboveFloorDb, other.PeakAboveFloorDb),
                        TimeSpanExtensions.Max(cur.LongestVoicedRun, other.LongestVoicedRun));
                    list[into] = merged;
                    list.RemoveAt(i);
                    changed = true;
                    i = Math.Max(-1, Math.Min(i, into) - 1);
                }
            }
            return list.Select((s, k) => new SpeechSegment(k, s.StartTime, s.EndTime, s.Kind, s.PeakLevel, s.PeakAboveFloorDb, s.LongestVoicedRun)).ToList();
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

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

        // ---------------------------------------------------------------------
        // Navigation
        //  - edges ("where could speech start / end"): every piece that is not silence
        //  - speech ("is there speech here"): the Speech pieces only
        // ---------------------------------------------------------------------

        private List<SpeechSegment> r_pieces = new List<SpeechSegment>();
        private List<TimeSpan> r_pieceStarts = new List<TimeSpan>();
        private List<TimeSpan> r_pieceEnds = new List<TimeSpan>();
        private List<SpeechSegment> r_speech = new List<SpeechSegment>();
        private List<TimeSpan> r_speechStarts = new List<TimeSpan>();
        private List<TimeSpan> r_speechEnds = new List<TimeSpan>();

        private void BuildNavigationIndex()
        {
            // Dips are the edges of the Speech pieces only (nothing before / after a Breath or a Click)
            r_speech = Segments.Where(s => s.IsSpeech).ToList();
            r_pieces = r_speech;
            r_pieceStarts = r_pieces.Select(s => s.StartTime).ToList();
            r_pieceEnds = r_pieces.Select(s => s.EndTime).ToList();
            r_speechStarts = r_speech.Select(s => s.StartTime).ToList();
            r_speechEnds = r_speech.Select(s => s.EndTime).ToList();
        }

        private List<TimeSpan> DipList(BoundarySide side) => side == BoundarySide.Start ? r_pieceStarts : r_pieceEnds;

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

        public IEnumerable<TimeSpan> GetDips(BoundarySide side, TimeSpan from, TimeSpan to)
        {
            var list = DipList(side);
            var minT = from < to ? from : to;
            var maxT = from < to ? to : from;
            for (int i = LastIndexLess(list, minT) + 1; i < list.Count && list[i] <= maxT; i++)
                yield return list[i];
        }

        /// <summary>True when 'dip' is an edge between something and silence (not a cut between two touching pieces).</summary>
        public bool IsSpeechEdge(TimeSpan dip)
        {
            bool before = IndexEndingAt(r_pieceEnds, dip) >= 0;
            bool after = IndexStartingAt(r_pieceStarts, dip) >= 0;
            return before != after;
        }

        /// <summary>True when 'time' is not inside a Speech piece (a Breath or a Click is "silent" here).</summary>
        public bool IsSilent(TimeSpan time)
        {
            int i = LastIndexLess(r_speechStarts, time + TimeSpan.FromTicks(1));
            return i < 0 || time >= r_speechEnds[i];
        }

        /// <summary>Returns true if any Speech piece overlaps the open interval (from, to).</summary>
        public bool HasSpeechBetween(TimeSpan from, TimeSpan to)
        {
            if (to <= from || r_speech.Count == 0)
                return false;
            int i = LastIndexLess(r_speechStarts, to);
            return i >= 0 && r_speechEnds[i] > from;
        }

        /// <summary>The Speech pieces overlapping [from, to] (not clipped), in order. Touching pieces are returned as one span.</summary>
        public List<(TimeSpan Start, TimeSpan End)> SpeechSpans(TimeSpan from, TimeSpan to)
        {
            var result = new List<(TimeSpan Start, TimeSpan End)>();
            if (to <= from)
                return result;

            for (int i = LastIndexLess(r_speechStarts, to); i >= 0 && r_speechEnds[i] > from; i--)
                result.Add((r_speechStarts[i], r_speechEnds[i]));
            result.Reverse();

            var merged = new List<(TimeSpan Start, TimeSpan End)>(result.Count);
            foreach (var span in result)
            {
                if (merged.Count > 0 && merged[merged.Count - 1].End >= span.Start)
                    merged[merged.Count - 1] = (merged[merged.Count - 1].Start, span.End);
                else
                    merged.Add(span);
            }
            return merged;
        }

        /// <summary>The gaps of at least 'minDuration' between two Speech spans overlapping [from, to].</summary>
        public List<(TimeSpan Start, TimeSpan End)> SilencesBetweenSpeech(TimeSpan from, TimeSpan to, TimeSpan minDuration)
        {
            var spans = SpeechSpans(from, to);
            var result = new List<(TimeSpan Start, TimeSpan End)>();
            for (int i = 1; i < spans.Count; i++)
            {
                if (spans[i].Start - spans[i - 1].End >= minDuration)
                    result.Add((spans[i - 1].End, spans[i].Start));
            }
            return result;
        }

        /// <summary>Total length of the Speech pieces inside [from, to].</summary>
        public TimeSpan SpeechDurationBetween(TimeSpan from, TimeSpan to)
        {
            var total = TimeSpan.Zero;
            if (to <= from)
                return total;

            for (int i = LastIndexLess(r_speechStarts, to); i >= 0 && r_speechEnds[i] > from; i--)
            {
                var start = r_speechStarts[i] > from ? r_speechStarts[i] : from;
                var end = r_speechEnds[i] < to ? r_speechEnds[i] : to;
                if (end > start)
                    total += end - start;
            }
            return total;
        }

        /// <summary>
        /// Moves a boundary inward over what is not speech: End goes back to the end of the last Speech piece before it,
        /// Start goes forward to the start of the first Speech piece after it.
        /// Returns 'time' unchanged when it is inside a Speech piece or when there is no Speech piece on that side.
        /// </summary>
        public TimeSpan TrimSilence(TimeSpan time, BoundarySide side)
        {
            if (r_speech.Count == 0)
                return time;

            if (side == BoundarySide.End)
            {
                int i = LastIndexLess(r_speechStarts, time);
                return i >= 0 && r_speechEnds[i] < time ? r_speechEnds[i] : time;
            }

            int j = FirstIndexGreater(r_speechEnds, time);
            return j < r_speech.Count && r_speechStarts[j] > time ? r_speechStarts[j] : time;
        }

        private static int IndexStartingAt(List<TimeSpan> starts, TimeSpan time)
        {
            int i = LastIndexLess(starts, time + TimeSpan.FromTicks(1));
            return (i >= 0 && starts[i] == time) ? i : -1;
        }

        private static int IndexEndingAt(List<TimeSpan> ends, TimeSpan time)
        {
            int i = LastIndexLess(ends, time + TimeSpan.FromTicks(1));
            return (i >= 0 && ends[i] == time) ? i : -1;
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

        /// <summary>Speech pieces are labelled A, B, C... (so neighbours differ), Breath "~", Click "'". Silence is not output.</summary>
        public IEnumerable<Subtitle> ToSegmentSubtitles()
        {
            var oneMs = TimeSpan.FromMilliseconds(1);
            int speechIndex = 0;
            var Segments = this.DetailedSegments;
            for (int i = 0; i < Segments.Count; i++)
            {
                var seg = Segments[i];
                string label;
                TimeSpan endTime = (i < Segments.Count - 1 && seg.Duration > oneMs) ? seg.EndTime - oneMs : seg.EndTime;
                switch (seg.Kind)
                {
                    case SegmentKind.Silence:
                        label = "";
                        //yield return new Subtitle(seg.StartTime, endTime, new[] { label });
                        break;
                    case SegmentKind.Speech:
                        label = ((char)('A' + (speechIndex++ % 26))).ToString();
                        yield return new Subtitle(seg.StartTime, endTime, new[] { label });
                        break;
                    case SegmentKind.Breath:
                        label = "~";
                        yield return new Subtitle(seg.StartTime, endTime, new[] { label });
                        break;
                    case SegmentKind.Click:
                        label = "'";
                        //yield return new Subtitle(seg.StartTime, endTime, new[] { label });
                        break;
                    default: continue;
                }
            }
        }
    }
}
