using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>
    /// Checks done on the final result (after the refining and the overlap handling).
    /// <see cref="PlanSplits"/> first: the long subtitles whose LongWithSilence validation is set to Fix are split (the task refines the parts).
    /// Then <see cref="Run"/> flags what is left.
    /// A check whose threshold is not set in <see cref="TimingChecksSettings"/> is not done.
    /// </summary>
    public class TimingValidator
    {
        /// <summary>Below this much speech, a characters-per-speech-second rate means nothing.</summary>
        private static readonly TimeSpan MinSpeechForRate = TimeSpan.FromSeconds(0.2);

        /// <summary>RepeatedText: how many of the following subtitles each subtitle is compared with.</summary>
        private const int RepeatedTextWindow = 2;

        private readonly TimingRefiner r_refiner;
        private readonly TimingChecksSettings r_checks;
        private readonly Regex r_maybePointless;

        public TimingValidator(TimingRefiner refiner, TimingValidatorSettings settings)
        {
            r_refiner = refiner ?? throw new ArgumentNullException(nameof(refiner));
            r_checks = settings?.Checks ?? new TimingChecksSettings();
            r_maybePointless = r_checks.CreateMaybePointlessRegex();
        }

        /// <summary>The long subtitles to split (nothing when LongWithSilence is not set to Fix). Nothing is flagged here.</summary>
        public List<TimingEdit> PlanSplits()
        {
            var edits = new List<TimingEdit>();
            if (r_refiner.ModeOf(ValidationCode.LongWithSilence) != ValidationMode.Fix)
                return edits;

            foreach (var cand in r_refiner.Candidates.Where(c => c.Origin == TimingOrigin.Original))
            {
                var silences = FindSplittableSilences(cand);
                if (silences.Count == 0)
                    continue;

                var cuts = ChooseCuts(cand.Start.Current, cand.End.Current, silences);
                if (cuts.Count == 0)
                    continue;

                var edit = new TimingEdit { Replaces = cand, Origin = TimingOrigin.SplitPart };
                var partStart = cand.Start.Current;
                foreach (var cut in cuts.OrderBy(c => c.Start))
                {
                    edit.Spans.Add((partStart, cut.Start));
                    partStart = cut.End;
                }
                edit.Spans.Add((partStart, cand.End.Current));
                edits.Add(edit);
            }
            return edits;
        }

        /// <summary>Flags everything the checks find.</summary>
        public void Run()
        {
            // The title cards have no text and no speech to check
            var candidates = r_refiner.Candidates.Where(c => c.Origin != TimingOrigin.TitleCard).ToList();

            foreach (var cand in candidates)
            {
                CheckOrigin(cand);
                CheckSpeechInside(cand);
                CheckDuration(cand);
                CheckTextLoop(cand);
                CheckMaybePointless(cand);
                CheckLongWithSilence(cand);
            }

            CheckGaps(candidates);
            CheckRepeatedText(candidates);
        }

        // ---------------------------------------------------------------------
        // One candidate
        // ---------------------------------------------------------------------

        /// <summary>What the split leaves behind: the text of the parts comes from the AI, so they are worth a look.</summary>
        private void CheckOrigin(TimingCandidate cand)
        {
            if (cand.Origin != TimingOrigin.SplitPart)
                return;

            r_refiner.Flag(cand, ValidationCode.SplitPart, "Part of a subtitle that was split (the text comes from the AI)");

            if (string.IsNullOrWhiteSpace(cand.FinalText))
                r_refiner.Flag(cand, ValidationCode.EmptyText, "The AI did not give any text");
        }

        private void CheckSpeechInside(TimingCandidate cand)
        {
            var speech = r_refiner.TerrainMapping.SpeechDurationBetween(cand.Start.Current, cand.End.Current);
            if (speech <= TimeSpan.Zero)
            {
                r_refiner.Flag(cand, ValidationCode.NoSpeechInside, "No speech found between the start and the end");
                return; // the rates below mean nothing
            }

            // Letters and digits only: no language rule, punctuation and spaces do not count
            int chars = (cand.FinalText ?? "").Count(char.IsLetterOrDigit);
            double speechSeconds = (speech > MinSpeechForRate ? speech : MinSpeechForRate).TotalSeconds;
            double rate = chars / speechSeconds;

            if (r_checks.MaxCharsPerSpeechSecond.HasValue && rate > r_checks.MaxCharsPerSpeechSecond.Value)
            {
                r_refiner.Flag(cand, ValidationCode.SpeechRateHigh,
                    $"Text too long for the speech ({chars} chars in {speech.TotalSeconds:0.00}s of speech = {rate:0.0}/s, max {r_checks.MaxCharsPerSpeechSecond.Value:0.0}/s)");
            }
            if (r_checks.MinCharsPerSpeechSecond.HasValue && rate < r_checks.MinCharsPerSpeechSecond.Value)
            {
                r_refiner.Flag(cand, ValidationCode.SpeechRateLow,
                    $"Text too short for the speech ({chars} chars in {speech.TotalSeconds:0.00}s of speech = {rate:0.0}/s, min {r_checks.MinCharsPerSpeechSecond.Value:0.0}/s)");
            }
        }

        private void CheckDuration(TimingCandidate cand)
        {
            var duration = cand.End.Current - cand.Start.Current;

            if (r_checks.MinDuration.HasValue && duration < r_checks.MinDuration.Value)
            {
                r_refiner.Flag(cand, ValidationCode.TooShort,
                    $"Too short ({duration.TotalSeconds:0.00}s, min {r_checks.MinDuration.Value.TotalSeconds:0.00}s)");
            }

            if (r_checks.MaxCharsPerSecond.HasValue && duration > TimeSpan.Zero)
            {
                int chars = (cand.FinalText ?? "").Count(char.IsLetterOrDigit);
                double rate = chars / duration.TotalSeconds;
                if (rate > r_checks.MaxCharsPerSecond.Value)
                {
                    r_refiner.Flag(cand, ValidationCode.TooFast,
                        $"Too fast ({chars} chars in {duration.TotalSeconds:0.00}s = {rate:0.0}/s, max {r_checks.MaxCharsPerSecond.Value:0.0}/s)");
                }
            }
        }

        /// <summary>A subtitle that is only filler words said over and over ("すごい", "ああ", "きもちいい"...): maybe not worth a subtitle.</summary>
        private void CheckMaybePointless(TimingCandidate cand)
        {
            if (r_maybePointless == null)
                return;

            var text = (cand.FinalText ?? "").Trim();
            if (text.Length == 0 || !r_maybePointless.IsMatch(text))
                return;

            var name = r_checks.FindMaybePointlessPatternName(text);
            r_refiner.Flag(cand, ValidationCode.MaybePointless,
                $"Maybe pointless: only filler words ({name ?? "combination of several"}): `{text}`");
        }

        /// <summary>A short unit repeated over and over inside one subtitle: the classic hallucination.</summary>
        private void CheckTextLoop(TimingCandidate cand)
        {
            if (TextSimilarity.IsRepetitionLoop(cand.FinalText))
                r_refiner.Flag(cand, ValidationCode.TextLoop, "The text repeats the same words over and over");
        }

        // ---------------------------------------------------------------------
        // Long subtitle with a silence inside: flag it, or split it
        // ---------------------------------------------------------------------

        /// <summary>The silences where the candidate could be split: none unless it is longer than MaxSubtitleDuration.</summary>
        private List<(TimeSpan Start, TimeSpan End)> FindSplittableSilences(TimingCandidate cand)
        {
            if (!r_checks.MaxSubtitleDuration.HasValue
                || cand.End.Current - cand.Start.Current <= r_checks.MaxSubtitleDuration.Value)
            {
                return new List<(TimeSpan Start, TimeSpan End)>();
            }

            return r_refiner.TerrainMapping.SilencesBetweenSpeech(cand.Start.Current, cand.End.Current, r_checks.MinSilenceToSplit);
        }

        /// <summary>What is still long with a silence inside after the split (a part is never split again, or the validation is not set to Fix).</summary>
        private void CheckLongWithSilence(TimingCandidate cand)
        {
            var silences = FindSplittableSilences(cand);
            if (silences.Count == 0)
                return;

            var duration = cand.End.Current - cand.Start.Current;
            var longest = silences.Max(s => s.End - s.Start);
            r_refiner.Flag(cand, ValidationCode.LongWithSilence,
                $"{duration.TotalSeconds:0.0}s long (max {r_checks.MaxSubtitleDuration.Value.TotalSeconds:0.0}s) with {silences.Count} silence(s) inside, up to {longest.TotalSeconds:0.0}s");
        }

        private List<(TimeSpan Start, TimeSpan End)> ChooseCuts(
            TimeSpan start,
            TimeSpan end,
            List<(TimeSpan Start, TimeSpan End)> silences)
        {
            if (r_checks.SplitStrategy == SplitStrategy.EverySilence)
                return silences;

            var cuts = new List<(TimeSpan Start, TimeSpan End)>();
            CutUnderMaxDuration(start, end, silences, cuts);
            return cuts;
        }

        /// <summary>Cuts the range at the silence nearest its middle, then does the same on each side, until every part is short enough.</summary>
        private void CutUnderMaxDuration(
            TimeSpan from,
            TimeSpan to,
            List<(TimeSpan Start, TimeSpan End)> silences,
            List<(TimeSpan Start, TimeSpan End)> cuts)
        {
            if (to - from <= r_checks.MaxSubtitleDuration.Value)
                return;

            var inside = silences.Where(s => s.Start > from && s.End < to).ToList();
            if (inside.Count == 0)
                return;

            var middle = from + TimeSpan.FromTicks((to - from).Ticks / 2);
            var best = inside
                .OrderBy(s => (s.Start + TimeSpan.FromTicks((s.End - s.Start).Ticks / 2) - middle).Duration())
                .First();

            cuts.Add(best);
            CutUnderMaxDuration(from, best.Start, inside, cuts);
            CutUnderMaxDuration(best.End, to, inside, cuts);
        }

        // ---------------------------------------------------------------------
        // Between candidates (the gaps use the final timings)
        // ---------------------------------------------------------------------

        private void CheckGaps(List<TimingCandidate> candidates)
        {
            if (!r_checks.MinGap.HasValue || candidates.Count == 0)
                return;

            // 'coveredUntil' = the latest end so far, so a long subtitle covering the next ones is handled
            var ordered = candidates.OrderBy(c => c.Start.Current).ToList();
            var coveredUntil = ordered[0].End.Current;
            var last = ordered[0];
            for (int i = 1; i < ordered.Count; i++)
            {
                var next = ordered[i];
                var gap = next.Start.Current - coveredUntil;

                if (gap > TimeSpan.Zero && gap < r_checks.MinGap.Value)
                {
                    var message = $"Gap with the adjacent subtitle is only {gap.TotalMilliseconds:0} ms (min {r_checks.MinGap.Value.TotalMilliseconds:0} ms)";
                    r_refiner.Flag(last, ValidationCode.TinyGap, message);
                    r_refiner.Flag(next, ValidationCode.TinyGap, message);
                }

                if (next.End.Current > coveredUntil)
                {
                    coveredUntil = next.End.Current;
                    last = next;
                }
            }
        }

        // ---------------------------------------------------------------------
        // Text
        // ---------------------------------------------------------------------

        /// <summary>A subtitle (long enough) that is the same, or nearly, as one of the next two.</summary>
        private void CheckRepeatedText(List<TimingCandidate> candidates)
        {
            if (!r_checks.RepeatedTextMinSimilarity.HasValue)
                return;

            var ordered = candidates.OrderBy(c => c.Start.Current).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var text = TextSimilarity.Normalize(ordered[i].FinalText);
                if (text.Length < r_checks.RepeatedTextMinChars)
                    continue;

                for (int j = i + 1; j < ordered.Count && j <= i + RepeatedTextWindow; j++)
                {
                    var other = TextSimilarity.Normalize(ordered[j].FinalText);
                    if (other.Length < r_checks.RepeatedTextMinChars)
                        continue;

                    var ratio = TextSimilarity.Ratio(text, other);
                    if (ratio >= r_checks.RepeatedTextMinSimilarity.Value)
                    {
                        var message = $"Same text as a nearby subtitle ({ratio:P0} similar)";
                        r_refiner.Flag(ordered[i], ValidationCode.RepeatedText, message);
                        r_refiner.Flag(ordered[j], ValidationCode.RepeatedText, message);
                    }
                }
            }
        }
    }
}
