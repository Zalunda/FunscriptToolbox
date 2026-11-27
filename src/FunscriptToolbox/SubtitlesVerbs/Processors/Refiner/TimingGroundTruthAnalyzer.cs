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
            /// <summary>The user kept the boundary: the refiner was right.</summary>
            Unchanged,

            /// <summary>The AI chose what the user wants, but the final boundary is not that (silence trim, overlaps, validation...).</summary>
            PostProcessed,

            /// <summary>The terrain offered the user's time to the AI (in at least one round), and the AI chose another one.</summary>
            OfferedNotChosen,

            /// <summary>The terrain found a dip at the user's time, but it was never offered to the AI (eliminated by the search, or not sampled).</summary>
            DipNeverOffered,

            /// <summary>The terrain found a dip at the user's time, but a hard limit (previous/next subtitle...) forbade it.</summary>
            BlockedByHardLimit,

            /// <summary>The user's time is inside the searched area, but the terrain has no dip near it.</summary>
            NoDipNearby,

            /// <summary>The user's time is outside of the searched area (further than the options of the terrain go).</summary>
            OutsideWindow,

            /// <summary>The boundary has no dips at all (nothing was snapped on the terrain).</summary>
            NoDips,

            /// <summary>The boundary was already validated (outer edge of a split subtitle): not judged.</summary>
            Fixed,
        }

        private static readonly Dictionary<Category, string> r_descriptions = new Dictionary<Category, string>
        {
            { Category.Unchanged, "User kept the boundary (refiner was right)" },
            { Category.PostProcessed, "AI chose the user's time, but post-processing changed it" },
            { Category.OfferedNotChosen, "User's time WAS offered to the AI, but not chosen" },
            { Category.DipNeverOffered, "Terrain had a dip at the user's time, but it was never offered" },
            { Category.BlockedByHardLimit, "Terrain had a dip at the user's time, but a hard limit forbade it" },
            { Category.NoDipNearby, "Terrain has no dip at the user's time (inside the searched area)" },
            { Category.OutsideWindow, "User's time is outside of the searched area" },
            { Category.NoDips, "Boundary had no dips (not snapped on the terrain)" },
            { Category.Fixed, "Boundary was fixed (not judged)" },
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

            /// <summary>What the AI chose on its last round (null when it chose nothing): the boundary before the post-processing.</summary>
            public TimeSpan? AiTime;

            /// <summary>The AI was not on the user's time, but the final boundary is (the post-processing fixed it).</summary>
            public bool PostProcessingFixed;

            /// <summary>The AI was not on the user's time, and the final boundary is clearly closer to it (but still not on it).</summary>
            public bool PostProcessingImproved;

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

            var aiRound = cand.Rounds.LastOrDefault(r => r.Side == side && r.Choice != null && r.Options.ContainsKey(r.Choice));
            if (aiRound != null)
            {
                result.AiTime = aiRound.Options[aiRound.Choice];
                if (!IsSame(userTime, result.AiTime.Value))
                {
                    result.PostProcessingFixed = IsSame(userTime, boundary.Current);
                    result.PostProcessingImproved = !result.PostProcessingFixed
                        && (boundary.Current - userTime).Duration() + SameTolerance < (result.AiTime.Value - userTime).Duration();
                }
            }

            if (IsSame(userTime, boundary.Current))
                return With(result, Category.Unchanged, result.PostProcessingFixed
                    ? $"AI chose {aiRound.Choice} ({Fmt(result.AiTime.Value)}), the post-processing moved it to the user's time ({Fmt(boundary.Current)})"
                    : "");

            if (boundary.Fixed)
                return With(result, Category.Fixed, "");

            // What was offered to the AI, in all the rounds of this side
            var rounds = cand.Rounds.Where(r => r.Side == side).ToList();
            var offered = rounds
                .SelectMany(r => r.Options.Select(o => (Round: r, Id: o.Key, Time: o.Value)))
                .ToList();

            // 1. The last choice of the AI is what the user wants
            var lastRound = rounds.LastOrDefault(r => r.Choice != null && r.Options.ContainsKey(r.Choice));
            if (lastRound != null && IsSame(userTime, lastRound.Options[lastRound.Choice]))
                return With(result, Category.PostProcessed,
                    $"AI chose {lastRound.Choice} ({Fmt(lastRound.Options[lastRound.Choice])}), final is {Fmt(boundary.Current)}");

            // 2. Offered to the AI
            var offeredNearest = offered.OrderBy(o => (o.Time - userTime).Duration()).FirstOrDefault();
            if (offeredNearest.Round != null && IsSame(userTime, offeredNearest.Time))
            {
                var inRounds = offered
                    .Where(o => IsSame(userTime, o.Time))
                    .Select(o => $"round {o.Round.RoundNumber}: {o.Id} {Fmt(o.Time)}")
                    .Distinct();
                return With(result, Category.OfferedNotChosen,
                    $"Offered ({string.Join(", ", inRounds)}), AI chose {lastRound?.Choice ?? "?"} ({(lastRound != null ? Fmt(lastRound.Options[lastRound.Choice]) : "?")})");
            }

            // 3. In the dips of the terrain, but never offered
            if (boundary.Dips.Count == 0 && offered.Count == 0)
                return With(result, Category.NoDips, "");

            var dipNearest = boundary.Dips.OrderBy(d => (d.Time - userTime).Duration()).FirstOrDefault();
            if (dipNearest != null && IsSame(userTime, dipNearest.Time))
            {
                return With(result,
                    dipNearest.State == DipCandidateState.HardLimit ? Category.BlockedByHardLimit : Category.DipNeverOffered,
                    $"Dip {Fmt(dipNearest.Time)} state={dipNearest.State}");
            }

            // 4. Outside or inside of the searched area?
            var searched = boundary.Dips.Select(d => d.Time).Concat(offered.Select(o => o.Time)).ToList();
            var min = searched.Min();
            var max = searched.Max();
            var nearestAny = searched.OrderBy(t => (t - userTime).Duration()).First();
            if (userTime < min || userTime > max)
            {
                var beyond = userTime < min ? min - userTime : userTime - max;
                var direction = (side == BoundarySide.Start) == (userTime < min) ? "outward" : "inward";
                if (terrain != null)
                {
                    // The dip of the terrain nearest to the user's time, that was out of reach of the search
                    var available = terrain.GetDips(side, userTime - CoverageSearchRange, userTime + CoverageSearchRange)
                        .Select(d => (Time: d, Distance: (d - userTime).Duration()))
                        .OrderBy(d => d.Distance)
                        .FirstOrDefault();
                    if (available.Time != TimeSpan.Zero)
                        result.Markers.Add((available.Time, $"[{side}] dip out of the searched area, nearest to USER: {Fmt(available.Time)} ({SignedMs(available.Time - userTime)} from USER)"));
                }
                return With(result, Category.OutsideWindow,
                    $"{beyond.TotalSeconds:0.000}s {direction} beyond the searched area [{Fmt(min)} .. {Fmt(max)}]" +
                    (boundary.OutwardLimit.HasValue ? $", dips dropped beyond {Fmt(boundary.OutwardLimit.Value)}" : ""));
            }

            var before = searched.Where(t => t < userTime).DefaultIfEmpty(TimeSpan.MinValue).Max();
            var after = searched.Where(t => t >= userTime).DefaultIfEmpty(TimeSpan.MinValue).Min();
            if (before != TimeSpan.MinValue)
                result.Markers.Add((before, $"[{side}] closest dip/option before USER: {Fmt(before)} ({SignedMs(before - userTime)} from USER)"));
            if (after != TimeSpan.MinValue)
                result.Markers.Add((after, $"[{side}] closest dip/option after USER: {Fmt(after)} ({SignedMs(after - userTime)} from USER)"));

            return With(result, Category.NoDipNearby,
                $"Nearest dip/option {Fmt(nearestAny)} is {(nearestAny - userTime).Duration().TotalMilliseconds:0}ms away");
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

        /// <summary>The categories where the user's time was not reached, merged in two files (by the sign of the delta) instead of one per category.</summary>
        private static readonly HashSet<Category> r_mergedCategories = new HashSet<Category>
        {
            Category.NoDipNearby, Category.OfferedNotChosen, Category.OutsideWindow, Category.PostProcessed,
        };

        private void Export(List<BoundaryResult> results)
        {
            var notUnchanged = results.Where(r => r.Category != Category.Unchanged).ToList();

            // What the post-processing did right: the AI was off, the final boundary is on the user's time (or clearly closer to it)
            Save(results.Where(r => r.PostProcessingFixed || r.PostProcessingImproved), "PostProcessing-Helped");

            // One file per (side, category) for the rare categories
            foreach (var group in notUnchanged
                .Where(r => !r_mergedCategories.Contains(r.Category))
                .GroupBy(r => (r.Side, r.Category)))
            {
                Save(group, $"{group.Key.Side}-{group.Key.Category}");
            }

            // Two files for the others: "+REFINER" = the refiner covers more than the user (too much audio),
            // "-REFINER" = some audio of the user's subtitles might be lost
            var merged = notUnchanged.Where(r => r_mergedCategories.Contains(r.Category)).ToList();
            Save(merged.Where(r => r.Delta >= TimeSpan.Zero), "+REFINER");
            Save(merged.Where(r => r.Delta < TimeSpan.Zero), "-REFINER");
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

                // The post-processing (silence trim, overlaps, validation...) compared with the choice of the AI
                var withAi = sideResults.Where(x => x.AiTime.HasValue && x.Category != Category.Fixed).ToList();
                if (withAi.Count > 0)
                {
                    string Pct(int n) => $"{n,5} ({n * 100.0 / withAi.Count,5:0.0}%)";
                    int fixedByPost = withAi.Count(x => x.PostProcessingFixed);
                    int improved = withAi.Count(x => x.PostProcessingImproved);
                    int ruined = withAi.Count(x => x.Category == Category.PostProcessed);
                    int aiOnUser = withAi.Count(x => IsSame(x.UserTime, x.AiTime.Value));
                    lines.Add($"    Post-processing, on the {withAi.Count} boundaries chosen by the AI ({aiOnUser} were on the user's time):");
                    lines.Add($"      helped, now on the user's time {Pct(fixedByPost)}   helped, closer {Pct(improved)}   made worse (AI was on the user's time) {Pct(ruined)}");
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
