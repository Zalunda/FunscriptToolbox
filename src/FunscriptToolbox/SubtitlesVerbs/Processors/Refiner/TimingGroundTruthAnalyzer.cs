using FunscriptToolbox.Core;
using FunscriptToolbox.Core.Infra;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Processors.Refiner
{
    /// <summary>
    /// Debug tool: compares the final timings of the refiner (and the choices that its terrain offered) with the same subtitles
    /// after the user fixed them by hand (the user's file is the truth), to find WHERE the refining went wrong.
    /// Each boundary (Start, then End) of each subtitle is put in one <see cref="Category"/>, and one .srt is exported per
    /// (side, category) so the cases can be looked at in the editor. Everything is done in the time of the whole timeline.
    /// Only the timings are compared (the subtitles added, deleted, merged or split by the user are only counted).
    /// </summary>
    internal class TimingGroundTruthAnalyzer
    {
        /// <summary>A user's time this close to another time is the same time.</summary>
        public static readonly TimeSpan SameTolerance = TimeSpan.FromMilliseconds(100);

        public enum Category
        {
            /// <summary>The user kept the boundary: the snapped timing was right.</summary>
            Unchanged,

            /// <summary>The terrain has a dip near the user's time, but snapping picked a different boundary.</summary>
            DipNearby,

            /// <summary>The terrain has no dip near the user's time.</summary>
            NoDipNearby,
        }

        private static readonly Dictionary<Category, string> r_descriptions = new Dictionary<Category, string>
        {
            { Category.Unchanged, "User kept the boundary (snapped timing was right)" },
            { Category.DipNearby, "Terrain had a dip at the user's time, but snapping picked another boundary" },
            { Category.NoDipNearby, "Terrain has no dip at the user's time" },
        };

        private class BoundaryResult
        {
            public TimingCandidate Candidate;
            public BoundarySide Side;
            public Subtitle UserSubtitle;
            public TimeSpan UserTime;
            public TimeSpan FinalTime;
            public Category Category;
            public string Detail;

            /// <summary>Micro subtitles added after the difference (ex: the dips closest to the user's time), to see them in the editor.</summary>
            public List<(TimeSpan Time, string Text)> Markers = new List<(TimeSpan, string)>();

            /// <summary>How much more the refiner covers than the user: + = nothing is missing (Start earlier / End later than the user), - = some audio of the user's subtitle might be missing.</summary>
            public TimeSpan Delta => Side == BoundarySide.End ? FinalTime - UserTime : UserTime - FinalTime;
        }

        private readonly SubtitleGeneratorContext r_context;
        private readonly string r_transcriptionId;
        private readonly Action<string> r_report;

        public TimingGroundTruthAnalyzer(SubtitleGeneratorContext context, string transcriptionId, Action<string> report)
        {
            r_context = context;
            r_transcriptionId = transcriptionId;
            r_report = report;
        }

        /// <param name="fileSuffix">Suffix of the files of the user (ex: ".manual-input.srt"), loaded in the time of the whole timeline.</param>
        public void Run(IReadOnlyList<TimingCandidate> candidates, AudioTerrainMapping terrain, string fileSuffix)
        {
            var truth = r_context.WIP.LoadVirtualSubtitleFile(fileSuffix);
            if (truth == null)
            {
                r_context.WriteInfo($"  GroundTruth: no '{fileSuffix}' file found: analysis skipped.");
                return;
            }

            var refined = candidates
                .Where(c => c.Origin != TimingOrigin.TitleCard)
                .OrderBy(c => c.Start.Current)
                .ToList();
            var matches = Match(refined, truth.Subtitles.OrderBy(s => s.StartTime).ToList());

            var results = new List<BoundaryResult>();
            foreach (var (cand, user) in matches)
            {
                foreach (var side in TimingCandidate.Sides)
                    results.Add(Classify(cand, side, user, terrain));
            }

            Export(results);
            Print(results, refined.Count, truth.Subtitles.Count, matches.Count, TerrainCoverage(matches, terrain));
        }

        // ---------------------------------------------------------------------
        // Terrain coverage (no AI involved: does the terrain have a dip where the user put the boundary?)
        // ---------------------------------------------------------------------

        private static readonly TimeSpan CoverageSearchRange = TimeSpan.FromSeconds(3);

        private static List<string> TerrainCoverage(List<(TimingCandidate Cand, Subtitle User)> matches, AudioTerrainMapping terrain)
        {
            var lines = new List<string>();
            if (terrain == null || matches.Count == 0)
                return lines;

            lines.Add("");
            lines.Add($"  TERRAIN COVERAGE (distance from the user's time to the nearest terrain dip, +/-{CoverageSearchRange.TotalSeconds:0}s, {terrain.Segments.Count(s => !s.IsSilence)} mountains):");
            foreach (var side in TimingCandidate.Sides)
            {
                var distances = new List<double>();
                foreach (var (_, user) in matches)
                {
                    var time = side == BoundarySide.Start ? user.StartTime : user.EndTime;
                    var nearest = terrain.GetDips(side, time - CoverageSearchRange, time + CoverageSearchRange)
                        .Select(d => (d - time).Duration().TotalMilliseconds)
                        .DefaultIfEmpty(double.PositiveInfinity)
                        .Min();
                    distances.Add(nearest);
                }

                distances.Sort();
                string Pct(double ms) => $"{distances.Count(d => d <= ms) * 100.0 / distances.Count,5:0.0}%";
                lines.Add($"    {side,-5} within 50ms: {Pct(50)}   100ms: {Pct(100)}   250ms: {Pct(250)}   500ms: {Pct(500)}   median: {distances[distances.Count / 2]:0}ms   90th pct: {distances[(int)(distances.Count * 0.9)]:0}ms");
            }
            return lines;
        }

        // ---------------------------------------------------------------------
        // Matching (the user only moved the timings: a subtitle of the user overlaps its candidate)
        // ---------------------------------------------------------------------

        private static List<(TimingCandidate Cand, Subtitle User)> Match(List<TimingCandidate> candidates, List<Subtitle> users)
        {
            var pairs = new List<(TimingCandidate Cand, Subtitle User, TimeSpan Overlap)>();
            foreach (var cand in candidates)
            {
                foreach (var user in users)
                {
                    if (user.StartTime >= cand.End.Current && user.StartTime >= cand.OriginalEndTime)
                        break; // sorted by start: nothing after this one can overlap
                    var overlap = TimeSpanExtensions.Max(
                        Overlap(cand.Start.Current, cand.End.Current, user.StartTime, user.EndTime),
                        Overlap(cand.OriginalStartTime, cand.OriginalEndTime, user.StartTime, user.EndTime));
                    if (overlap > TimeSpan.Zero)
                        pairs.Add((cand, user, overlap));
                }
            }

            // Biggest overlaps first: each candidate and each subtitle of the user is used only once
            var usedCands = new HashSet<TimingCandidate>();
            var usedUsers = new HashSet<Subtitle>();
            var result = new List<(TimingCandidate, Subtitle)>();
            foreach (var pair in pairs.OrderByDescending(p => p.Overlap))
            {
                if (usedCands.Contains(pair.Cand) || usedUsers.Contains(pair.User))
                    continue;
                usedCands.Add(pair.Cand);
                usedUsers.Add(pair.User);
                result.Add((pair.Cand, pair.User));
            }
            return result.OrderBy(r => r.Item1.Start.Current).ToList();
        }

        private static TimeSpan Overlap(TimeSpan s1, TimeSpan e1, TimeSpan s2, TimeSpan e2)
        {
            var overlap = TimeSpanExtensions.Min(e1, e2) - TimeSpanExtensions.Max(s1, s2);
            return overlap > TimeSpan.Zero ? overlap : TimeSpan.Zero;
        }

        // ---------------------------------------------------------------------
        // Classification
        // ---------------------------------------------------------------------

        private static bool IsSame(TimeSpan a, TimeSpan b) => (a - b).Duration() <= SameTolerance;

        private static BoundaryResult Classify(TimingCandidate cand, BoundarySide side, Subtitle user, AudioTerrainMapping terrain)
        {
            var boundary = cand[side];
            var result = new BoundaryResult
            {
                Candidate = cand,
                Side = side,
                UserSubtitle = user,
                UserTime = side == BoundarySide.Start ? user.StartTime : user.EndTime,
                FinalTime = boundary.Current,
            };
            var userTime = result.UserTime;

            if (IsSame(userTime, boundary.Current))
                return With(result, Category.Unchanged, "");

            if (terrain != null)
            {
                var nearest = terrain.GetDips(side, userTime - CoverageSearchRange, userTime + CoverageSearchRange)
                    .Select(d => (Time: d, Distance: (d - userTime).Duration()))
                    .OrderBy(d => d.Distance)
                    .FirstOrDefault();

                if (nearest.Time != TimeSpan.Zero)
                {
                    result.Markers.Add((nearest.Time, $"[{side}] nearest terrain dip to USER: {Fmt(nearest.Time)} ({SignedMs(nearest.Time - userTime)} from USER)"));
                    if (nearest.Distance <= SameTolerance)
                        return With(result, Category.DipNearby, $"Terrain dip {Fmt(nearest.Time)} ({SignedMs(nearest.Time - userTime)} from USER)");

                    return With(result, Category.NoDipNearby, $"Nearest terrain dip {Fmt(nearest.Time)} is {nearest.Distance.TotalMilliseconds:0}ms away");
                }
            }

            return With(result, Category.NoDipNearby, "No terrain dip nearby");
        }

        private static BoundaryResult With(BoundaryResult result, Category category, string detail)
        {
            result.Category = category;
            result.Detail = detail;
            return result;
        }

        private static string SignedMs(TimeSpan t) => $"{t.TotalMilliseconds:+0;-0;0}ms";

        private static string Fmt(TimeSpan t) => $"{t:hh\\:mm\\:ss\\.fff}";

        // ---------------------------------------------------------------------
        // Exports
        // ---------------------------------------------------------------------

        private void Export(List<BoundaryResult> results)
        {
            var notUnchanged = results.Where(r => r.Category != Category.Unchanged).ToList();

            // Two files: "+REFINER" = the refiner covers more than the user (too much audio),
            // "-REFINER" = some audio of the user's subtitles might be lost
            Save(notUnchanged.Where(r => r.Delta >= TimeSpan.Zero), "+REFINER");
            Save(notUnchanged.Where(r => r.Delta < TimeSpan.Zero), "-REFINER");
        }

        private void Save(IEnumerable<BoundaryResult> results, string name)
        {
            var subtitles = results
                .OrderBy(r => r.UserSubtitle.StartTime)
                .ThenBy(r => r.Side)
                .SelectMany(r => new[] { ToDifference(r) }
                    .Concat(r.Markers.Select(m => new Subtitle(m.Time, m.Time + MinSpanDuration, new[] { m.Text })))
                    .Concat(new[] { ToSubtitle(r) }))
                .ToList();

            var virtualFile = r_context.WIP.CreateVirtualSubtitleFile();
            virtualFile.Subtitles.AddRange(subtitles);
            virtualFile.Save(
                r_context.WIP.ParentPath,
                $".Worker.{r_transcriptionId}.GROUNDTRUTH.{name}.srt",
                r_context.SoftDelete);
        }

        /// <summary>The subtitle of the user (so the time shown is the truth), with what the refiner did as text.</summary>
        private static Subtitle ToSubtitle(BoundaryResult r)
        {
            var lines = new List<string>
            {
                $"[{r.Side}] {r.Category}: {r.Detail}",
                $"  Refiner: {Fmt(r.FinalTime)}   User: {Fmt(r.UserTime)}   Delta: {r.Delta.TotalMilliseconds:+0;-0;0}ms",
            };
            lines.AddRange(r.Candidate.Traces);
            lines.Add("");
            lines.Add(r.Candidate.FinalText);
            return new Subtitle(r.UserSubtitle.StartTime, r.UserSubtitle.EndTime, lines.ToArray());
        }

        private static readonly TimeSpan MinSpanDuration = TimeSpan.FromMilliseconds(10);

        /// <summary>
        /// A subtitle spanning the gap between the two times that are judged (the user's time and the refiner's time), to see the
        /// difference in the editor. The text gives the delta and both times, the earliest first.
        /// </summary>
        private static Subtitle ToDifference(BoundaryResult r)
        {
            var first = TimeSpanExtensions.Min(r.UserTime, r.FinalTime);
            var last = TimeSpanExtensions.Max(r.UserTime, r.FinalTime);
            if (last - first < MinSpanDuration)
                last = first + MinSpanDuration;

            var times = r.FinalTime <= r.UserTime
                ? $"REFINER {Fmt(r.FinalTime)}, USER {Fmt(r.UserTime)}"
                : $"USER {Fmt(r.UserTime)}, REFINER {Fmt(r.FinalTime)}";
            return new Subtitle(first, last, new[] { $"[{r.Side}] ({r.Delta.TotalMilliseconds:+0;-0;0}ms to REFINER) {times}" });
        }

        // ---------------------------------------------------------------------
        // Report
        // ---------------------------------------------------------------------

        private void Print(List<BoundaryResult> results, int nbCandidates, int nbUserSubtitles, int nbMatched, List<string> coverageLines)
        {
            const string line = "================================================================================";
            var lines = new List<string>
            {
                line,
                $"  GROUND TRUTH ANALYSIS ({r_transcriptionId}), tolerance {SameTolerance.TotalMilliseconds:0}ms",
                line,
                $"  Refiner subtitles: {nbCandidates}, user subtitles: {nbUserSubtitles}, matched: {nbMatched}",
                $"  Not matched: {nbCandidates - nbMatched} deleted by the user (or title cards), {nbUserSubtitles - nbMatched} added/other (ignored)",
            };

            foreach (var side in TimingCandidate.Sides)
            {
                var sideResults = results.Where(r => r.Side == side).ToList();
                lines.Add("");
                lines.Add($"  {side.ToString().ToUpper()} ({sideResults.Count} boundaries):");
                foreach (Category category in Enum.GetValues(typeof(Category)))
                {
                    var inCategory = sideResults.Where(r => r.Category == category).ToList();
                    if (inCategory.Count == 0)
                        continue;
                    var text = $"    {category,-20}{inCategory.Count,5} ({inCategory.Count * 100.0 / Math.Max(1, sideResults.Count),5:0.0}%)  {r_descriptions[category]}";
                    if (category != Category.Unchanged)
                    {
                        var deltas = inCategory.Select(r => r.Delta.TotalSeconds).OrderBy(d => d).ToList();
                        text += $"  [median delta {deltas[deltas.Count / 2]:+0.000;-0.000;0.000}s]";
                    }
                    lines.Add(text);
                }
            }
            lines.AddRange(coverageLines);
            lines.Add(line);

            foreach (var l in lines)
                r_report(l);

            var path = r_context.WIP.BaseFilePath + $".Worker.{r_transcriptionId}.GROUNDTRUTH.report.txt";
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
        }
    }
}
