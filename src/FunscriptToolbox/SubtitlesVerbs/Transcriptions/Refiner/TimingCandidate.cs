using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.Core;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    public class TimingCandidate
    {
        /// <summary>The order in which the boundaries are refined.</summary>
        public static readonly BoundarySide[] Sides = { BoundarySide.Start, BoundarySide.End };

        public TimeSpan OriginalStartTime { get; set; }
        public TimeSpan OriginalEndTime { get; set; }
        public string OriginalText { get; set; }

        /// <summary>What the AI heard during the Start pass (null when it agreed with OriginalText).</summary>
        public string HeardText { get; set; }

        /// <summary>Best known text: what the AI heard if it disagreed with the draft, otherwise the draft.</summary>
        public TimingOrigin Origin { get; set; }

        /// <summary>True when the TextMismatch validation is set to Fix: HeardText replaces OriginalText in the output.</summary>
        public bool TextReplaced { get; set; }

        /// <summary>The text of the output: what the AI heard when it replaced the original, otherwise the original.</summary>
        [JsonIgnore] public string FinalText => TextReplaced && !string.IsNullOrWhiteSpace(HeardText) ? HeardText : OriginalText;

        [JsonIgnore] public string TextForAI => string.IsNullOrWhiteSpace(HeardText) ? OriginalText : HeardText;

        [JsonIgnore] public TimedItemWithMetadata OriginalItem { get; set; }

        public BoundaryState Start { get; } = new BoundaryState();
        public BoundaryState End { get; } = new BoundaryState();

        public BoundaryState this[BoundarySide side] => side == BoundarySide.Start ? Start : End;

        [JsonIgnore] public bool IsFullyLocked => Start.Locked && End.Locked;

        public string PossibleBleedFromPrev { get; set; }
        public string PossibleBleedFromNext { get; set; }

        public TimingRound CurrentRound { get; set; }
        public List<TimingRound> Rounds { get; set; } = new List<TimingRound>();

        public List<ValidationNote> ValidationNotes { get; set; } = new List<ValidationNote>();
        public List<string> Traces { get; set; } = new List<string>();

        public void LockAll()
        {
            Start.Locked = true;
            End.Locked = true;
        }

        public void AddValidationNote(ValidationNote note)
        {
            if (!ValidationNotes.Any(n => n.Code == note.Code && n.Message == note.Message))
                ValidationNotes.Add(note);
        }

        /// <summary>False only for a candidate that is fully settled and has nothing flagged: it goes in the CLEAN subtitles.</summary>
        [JsonIgnore] public bool NeedsReview => ValidationNotes.Count > 0 || !IsFullyLocked;

        public string GetLockTag()
        {
            if (Start.Locked && End.Locked) return "**";
            if (Start.Locked) return "S*";
            if (End.Locked) return "*E";
            return "??";
        }

        /// <summary>
        /// Adds a trace row showing the CURRENT bounds. The +/- modifiers are derived by comparing
        /// the bounds as they were before the change (prevStart / prevEnd) with the current ones.
        /// Pass the current values as "previous" to get no modifier.
        /// </summary>
        public void Trace(string step, TimeSpan prevStart, TimeSpan prevEnd, string result = null, string text = null)
        {
            AddTraceRow(
                step,
                GetStartMod(prevStart, Start.Current), Start.Current,
                End.Current, GetEndMod(prevEnd, End.Current),
                result, text);
        }

        /// <summary>Low-level trace row (used when the displayed bounds are not the current ones, e.g. "Orig").</summary>
        public void AddTraceRow(string step, char startMod, TimeSpan start, TimeSpan end, char endMod, string result = null, string text = null)
        {
            string lockTag = GetLockTag();
            string stepCol = (step ?? "").PadRight(4);
            string boundsCol = $"[{startMod}{start:hh\\:mm\\:ss\\.fff}=>{end:hh\\:mm\\:ss\\.fff}{endMod}]";

            if (string.IsNullOrEmpty(result) && string.IsNullOrEmpty(text))
            {
                Traces.Add($"{lockTag} {stepCol} {boundsCol}");
            }
            else
            {
                string resCol = (result ?? "").PadRight(24);
                string textCol = text != null ? $"'{text}'" : "";
                Traces.Add($"{lockTag} {stepCol} {boundsCol} {resCol} {textCol}".TrimEnd());
            }
        }

        public static char GetStartMod(TimeSpan prev, TimeSpan cur)
        {
            var diffMs = (cur - prev).TotalMilliseconds;
            if (Math.Abs(diffMs) < 1) return ' ';
            return diffMs > 0 ? '-' : '+';
        }

        public static char GetEndMod(TimeSpan prev, TimeSpan cur)
        {
            var diffMs = (cur - prev).TotalMilliseconds;
            if (Math.Abs(diffMs) < 1) return ' ';
            return diffMs < 0 ? '-' : '+';
        }

        public Subtitle ToSubtitle()
        {
            return new Subtitle(Start.Current, End.Current, new[] { FinalText });
        }

        /// <summary>The reasons the candidate needs attention (one line each), then its text.</summary>
        public Subtitle ToReviewSubtitle()
        {
            var lines = ValidationNotes.Select(n => n.ToString()).Append(FinalText).ToArray();
            return new Subtitle(Start.Current, End.Current, lines);
        }
    }
}
