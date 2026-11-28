using FunscriptToolbox.Core;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Processors
{
    /// <summary>
    /// Debug, for the tests of the retrier: the answers and the subtitles of a run compared with lines reviewed by hand (GroundTruthExtension),
    /// in '<scene>.Worker.<id>.run.report.txt' (next to the '.run.json').
    /// </summary>
    public partial class TranscriberAudioFullAIGoogleTranscribe
    {
        [ConfigDoc("Debug: the suffix of the files with the lines reviewed by hand, the truth (ex. \".manual-input.GOOD.srt\"). At the end of each run (with " +
            "ExportMetadataSrt), '<scene>.Worker.<id>.run.report.txt' compares the answers and the subtitles with them: the lines missed by each pass / " +
            "retry, by the merge, the extra subtitles, the duplicates left out... null: not done.")]
        [JsonProperty(Order = 70)]
        public string GroundTruthExtension { get; set; }

        /// <summary>A line of the truth is heard by an answer / in the subtitles when one of its phrases / subtitles is at most this far from it.</summary>
        private static readonly TimeSpan GroundTruthMargin = TimeSpan.FromMilliseconds(300);

        private sealed class TruthLine
        {
            public TimeSpan Start { get; set; }
            public TimeSpan End { get; set; }
            public string Text { get; set; }
        }

        private sealed class ReportAnswer
        {
            public RequestToDo Request { get; set; }
            public int Index { get; set; }
            public List<Phrase> Phrases { get; set; }
        }

        /// <summary>The text of a line of the truth: its lines that are not the traces / review notes of the preparer ('[...]', indented, '{...}').</summary>
        private static string GetTruthText(string text)
        {
            var kept = (text ?? string.Empty)
                .Replace("\r", string.Empty)
                .Split('\n')
                .Where(l => l.Trim().Length > 0 && !l.StartsWith(" ") && !l.StartsWith("[") && !l.StartsWith("{") && !l.StartsWith("-") && !l.StartsWith("Other"))
                .ToArray();
            return kept.Length == 0 ? "(timing without text)" : string.Join(" ", kept);
        }

        private void WriteGroundTruthReport(SubtitleGeneratorContext context, Track transcription, RunReport report, string status)
        {
            var path = context.WIP.BaseFilePath + $".Worker.{transcription.Id}.run.report.txt";
            var output = new List<string>();
            var truthFile = context.WIP.LoadVirtualSubtitleFile(this.GroundTruthExtension);
            if (truthFile == null)
            {
                File.WriteAllLines(path, new[] { $"No '{this.GroundTruthExtension}' file found next to the videos of the scene." }, new UTF8Encoding(true));
                return;
            }

            var truth = truthFile.Subtitles
                .OrderBy(s => s.StartTime)
                .Select(s => new TruthLine { Start = s.StartTime, End = s.EndTime, Text = GetTruthText(s.Text) })
                .ToList();
            string P(TimeSpan t) => $"[{context.WIP.TimelineMap.ConvertToPartSpecificFileIndexAndTime(t)}]";
            static string T(TimeSpan t) => t.ToString(@"hh\:mm\:ss\.fff");
            static bool Near(TimeSpan aStart, TimeSpan aEnd, TimeSpan bStart, TimeSpan bEnd) => aEnd + GroundTruthMargin > bStart && aStart - GroundTruthMargin < bEnd;
            static string Cut(string text, int length) => text == null ? string.Empty : text.Length <= length ? text : text.Substring(0, length) + "...";
            static string Percent(int part, int total) => total == 0 ? "-" : (100.0 * part / total).ToString("0.0", CultureInfo.InvariantCulture) + "%";

            // The answers of the run (each one once), with their phrases (moans left out, like the merge)
            var answers = report.Requests
                .Select((r, index) => (r, index))
                .Where(x => x.r.Result != null)
                .GroupBy(x => x.r.Result)
                .Select(g => new ReportAnswer { Request = g.First().r, Index = g.First().index, Phrases = BuildPhrases(context, g.Key, logIgnored: false) })
                .ToList();
            bool HeardBy(TruthLine line, ReportAnswer answer) => answer.Phrases.Any(p => Near(p.Item.StartTime, p.Item.EndTime, line.Start, line.End));
            List<TruthLine> MissedBy(IEnumerable<ReportAnswer> selection)
            {
                var list = selection.ToList();
                return truth.Where(line => !list.Any(a => HeardBy(line, a))).ToList();
            }
            string Describe(ReportAnswer a) => $"#{a.Index} {a.Request.Kind}{(a.Request.Pass != null ? $" pass {a.Request.Pass}" : string.Empty)} {T(a.Request.Timing.StartTime)}";

            var subtitles = report.LastItems;
            var missed = truth.Where(line => !subtitles.Any(s => Near(s.StartTime, s.EndTime, line.Start, line.End))).ToList();
            var extras = subtitles.Where(s => !truth.Any(line => Near(s.StartTime, s.EndTime, line.Start, line.End))).ToList();
            var missedByAllAnswers = MissedBy(answers);
            var lostByMerge = missed.Where(line => !missedByAllAnswers.Contains(line)).ToList();
            var duplicatesLosingALine = report.Duplicates
                .Where(d => missed.Any(line => Near(d.Dropped.Item.StartTime, d.Dropped.Item.EndTime, line.Start, line.End)))
                .ToList();

            // Header
            output.Add($"Run: {status}, {report.StartedAt:yyyy-MM-dd HH:mm:ss} to {DateTime.Now:HH:mm:ss}.");
            output.Add($"Settings: MaxChunkDuration {T(this.MaxChunkDuration)}, MainPassCount {this.MainPassCount}, BufferZone {T(this.BufferZone)}, " +
                $"RetryGapMinDuration {(this.RetryGapMinDuration is TimeSpan g ? T(g) : "null")}, PhraseMaxGap {T(this.PhraseMaxGap)}, " +
                $"MaxGap {(this.MaxGap is TimeSpan mg ? T(mg) : "null")}, MaxDuration {(this.MaxDuration is TimeSpan md ? T(md) : "null")}, " +
                $"patterns {this.TranscriptionToIgnorePatterns?.Length ?? 0}.");
            output.Add($"Truth: {truth.Count} lines ('{this.GroundTruthExtension}'), {truth.Count(l => l.Text.StartsWith("(timing"))} of them without text. Result: {subtitles.Count} subtitles.");
            output.Add(string.Empty);
            output.Add($"=> MISSED: {missed.Count} ({Percent(missed.Count, truth.Count)})   EXTRA (no line of the truth): {extras.Count}   " +
                $"LOST BY THE MERGE: {lostByMerge.Count}   DUPLICATES LEFT OUT: {report.Duplicates.Count} (a line of the truth lost: {duplicatesLosingALine.Count})");
            output.Add(string.Empty);

            // What each part of the run heard
            output.Add($"LINES MISSED BY EACH PART OF THE RUN (heard: a phrase of an answer at most {GroundTruthMargin.TotalMilliseconds:0} ms from the line, moans left out)");
            var mainAnswers = answers.Where(a => a.Request.Kind == ReadingKind.Chunk).ToList();
            foreach (var pass in mainAnswers.Select(a => a.Request.Pass ?? 1).Distinct().OrderBy(p => p))
                output.Add($"  pass {pass} alone                 : {MissedBy(mainAnswers.Where(a => (a.Request.Pass ?? 1) == pass)).Count}");
            output.Add($"  the main passes together     : {MissedBy(mainAnswers).Count}");
            output.Add($"  + the boundary retries       : {MissedBy(answers.Where(a => a.Request.Kind != ReadingKind.Gap)).Count}");
            output.Add($"  + the gap retries (all)      : {missedByAllAnswers.Count}");
            output.Add($"  the subtitles (after merge)  : {missed.Count}");
            output.Add(string.Empty);

            // The requests
            var sent = report.Requests.Where(r => r.Result != null && !r.FromCache).ToList();
            output.Add($"REQUESTS: {report.Requests.Count} ({sent.Count} sent, {report.Requests.Count(r => r.Result != null && r.FromCache)} from the job state, " +
                $"{report.Requests.Count(r => r.Result == null)} not sent), audio sent {T(TimeSpan.FromTicks(sent.Sum(r => r.Timing.Duration.Ticks)))}, " +
                $"estimated cost ${sent.Sum(r => r.Cost == null ? 0 : r.Cost.Input.EstimatedCost + r.Cost.Output.EstimatedCost).ToString("0.00", CultureInfo.InvariantCulture)}.");
            foreach (var kind in report.Requests.GroupBy(r => r.Kind))
                output.Add($"  {kind.Key,-9}: {kind.Count()} ({kind.Count(r => r.Result != null && !r.FromCache)} sent), phrases kept {report.LastAccepted.Count(a => a.Candidate.Kind == kind.Key)}");
            output.Add(string.Empty);

            // The main chunks
            output.Add("MAIN CHUNKS (lines of the truth inside the chunk / missed by it)");
            var byMinute = new SortedDictionary<int, (int Total, int Missed)>();
            foreach (var answer in mainAnswers.OrderBy(a => a.Request.Pass).ThenBy(a => a.Request.Timing.StartTime))
            {
                var chunk = answer.Request.Result;
                var inside = truth.Where(l => l.Start >= chunk.StartTime && l.End <= chunk.EndTime).ToList();
                var missedInside = inside.Where(l => !HeardBy(l, answer)).ToList();
                foreach (var line in inside)
                {
                    var minute = (int)(line.Start - chunk.StartTime).TotalMinutes;
                    var (total, nb) = byMinute.TryGetValue(minute, out var value) ? value : (0, 0);
                    byMinute[minute] = (total + 1, nb + (missedInside.Contains(line) ? 1 : 0));
                }
                var minutes = (chunk.EndTime - chunk.StartTime).TotalMinutes;
                var words = GetUsableWords(chunk, out _, out var nbRepeated);
                var notes = new List<string>();
                if (IsLooping(chunk))
                    notes.Add("LOOPING (ignored)");
                if (chunk.IncompleteReason != null)
                    notes.Add("incomplete");
                if (nbRepeated > 0)
                    notes.Add($"{nbRepeated} repeated words cut");
                if (answer.Request.Note != null && answer.Request.Note.StartsWith("early stop"))
                    notes.Add(answer.Request.Note);
                output.Add($"  pass {answer.Request.Pass ?? 1} {P(chunk.StartTime)}-{P(chunk.EndTime)} {answer.Request.Kind,-5} {(answer.Request.FromCache ? "cache" : "sent "),-5} " +
                    $"words {words.Count,5} ({(minutes > 0 ? words.Count / minutes : 0),5:0.0}/min)  truth {inside.Count,3}, missed {missedInside.Count,3} ({Percent(missedInside.Count, inside.Count),6})" +
                    (notes.Count > 0 ? "  " + string.Join(", ", notes) : string.Empty));
            }
            output.Add("  miss rate by minute in the chunk: " + string.Join("  ", byMinute.Select(kv => $"{kv.Key}: {Percent(kv.Value.Missed, kv.Value.Total)}")));
            output.Add(string.Empty);

            // The lines missed
            var tried = report.JobState.GapRetries.Select(gr => (gr.HoleStart, gr.HoleEnd)).ToList();
            output.Add($"LINES MISSED ({missed.Count})");
            var missedSrt = context.WIP.CreateVirtualSubtitleFile();
            foreach (var line in missed)
            {
                var heard = answers.Where(a => HeardBy(line, a)).ToList();
                var asked = tried.Any(t => t.HoleStart <= line.End && t.HoleEnd >= line.Start);
                var why = heard.Count > 0
                    ? $"HEARD BY {string.Join(", ", heard.Take(3).Select(Describe))} (lost by the merge)"
                    : $"heard by no answer{(asked ? " (a gap retry asked it)" : string.Empty)}";
                output.Add($"  {P(line.Start)} {Cut(line.Text, 50)}   {why}");
                missedSrt.Subtitles.Add(new Subtitle(line.Start, line.End, new[] { $"[MISSED] {why}", line.Text }));
            }
            output.Add(string.Empty);

            // The lines lost by the merge
            output.Add($"LOST BY THE MERGE ({lostByMerge.Count}: heard by an answer, not in the subtitles)");
            foreach (var line in lostByMerge)
            {
                output.Add($"  {P(line.Start)}-{T(line.End)} {Cut(line.Text, 50)}");
                foreach (var answer in answers.Where(a => HeardBy(line, a)).Take(4))
                {
                    var phrase = answer.Phrases.First(p => Near(p.Item.StartTime, p.Item.EndTime, line.Start, line.End));
                    var kept = report.LastAccepted.Any(a => a.Candidate.Phrase == phrase);
                    output.Add($"      {Describe(answer)}: {T(phrase.Item.StartTime)}-{T(phrase.Item.EndTime)} '{Cut(phrase.Item.Metadata.Get(this.MetadataProduced), 40)}'{(kept ? " (kept)" : string.Empty)}");
                }
                foreach (var subtitle in subtitles.Where(s => s.EndTime > line.Start - TimeSpan.FromSeconds(3) && s.StartTime < line.End + TimeSpan.FromSeconds(3)))
                    output.Add($"      subtitle {T(subtitle.StartTime)}-{T(subtitle.EndTime)} '{Cut(subtitle.Metadata.Get(this.MetadataProduced), 40)}'");
            }
            output.Add(string.Empty);

            // The duplicates left out
            output.Add($"DUPLICATES LEFT OUT ({report.Duplicates.Count}: the same text, at most {SameTextMaxDistance.TotalSeconds:0} s from a phrase of another answer)");
            string DescribeChunk(CachedChunk chunk) => answers.FirstOrDefault(a => a.Request.Result == chunk) is ReportAnswer a ? Describe(a) : T(chunk.StartTime);
            foreach (var (dropped, droppedChunk, kept, keptChunk) in report.Duplicates.OrderBy(d => d.Dropped.Item.StartTime))
            {
                var lost = missed.FirstOrDefault(line => Near(dropped.Item.StartTime, dropped.Item.EndTime, line.Start, line.End));
                output.Add($"  {P(dropped.Item.StartTime)} '{Cut(dropped.Item.Metadata.Get(this.MetadataProduced), 30)}' ({DescribeChunk(droppedChunk)})  =  " +
                    $"{T(kept.Item.StartTime)} '{Cut(kept.Item.Metadata.Get(this.MetadataProduced), 30)}' ({DescribeChunk(keptChunk)})" +
                    (lost != null ? $"   A LINE OF THE TRUTH LOST: '{Cut(lost.Text, 30)}'" : string.Empty));
            }
            output.Add(string.Empty);

            // The extra subtitles
            output.Add($"EXTRA SUBTITLES ({extras.Count}: no line of the truth at most {GroundTruthMargin.TotalMilliseconds:0} ms from them)");
            var extraSrt = context.WIP.CreateVirtualSubtitleFile();
            foreach (var extra in extras)
            {
                var closest = truth.OrderBy(l => l.Start > extra.EndTime ? l.Start - extra.EndTime : l.End < extra.StartTime ? extra.StartTime - l.End : TimeSpan.Zero).FirstOrDefault();
                var distance = closest == null ? TimeSpan.Zero : closest.Start > extra.EndTime ? closest.Start - extra.EndTime : extra.StartTime - closest.End;
                var sources = report.LastAccepted
                    .Where(a => a.Candidate.Item.StartTime >= extra.StartTime - TimeSpan.FromMilliseconds(1) && a.Candidate.Item.EndTime <= extra.EndTime + TimeSpan.FromMilliseconds(1))
                    .Select(a => DescribeChunk(a.Chunk))
                    .Distinct();
                var info = $"from {string.Join(", ", sources)}; " +
                    (closest == null ? "no line of the truth" : $"the closest line of the truth: {distance.TotalSeconds:0.0} s, '{Cut(closest.Text, 25)}'");
                output.Add($"  {P(extra.StartTime)}-{T(extra.EndTime)} {Cut(extra.Metadata.Get(this.MetadataProduced), 40)}   ({info})");
                extraSrt.Subtitles.Add(new Subtitle(extra.StartTime, extra.EndTime, new[] { $"[EXTRA] {info}", extra.Metadata.Get(this.MetadataProduced) ?? string.Empty }));
            }

            File.WriteAllLines(path, output, new UTF8Encoding(true));

            // The same, as .srt files next to the videos (one per part; to listen to them): the missed lines on the times of the truth, the extra
            // subtitles on their own times
            missedSrt.Save(context.WIP.ParentPath, $".Worker.{transcription.Id}.run.missed.srt", context.SoftDelete);
            extraSrt.Save(context.WIP.ParentPath, $".Worker.{transcription.Id}.run.extra.srt", context.SoftDelete);
        }
    }
}
