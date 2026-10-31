using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.Core;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    public class TimingCandidate
    {
        public static readonly BoundarySide[] Sides = { BoundarySide.Start, BoundarySide.End };

        public TimeSpan OriginalStartTime { get; set; }
        public TimeSpan OriginalEndTime { get; set; }
        public string OriginalText { get; set; }

        [JsonIgnore] public TimedItemWithMetadata OriginalItem { get; set; }

        public BoundaryState Start { get; } = new BoundaryState();
        public BoundaryState End { get; } = new BoundaryState();

        public BoundaryState this[BoundarySide side] => side == BoundarySide.Start ? Start : End;

        [JsonIgnore] public bool IsFullyLocked => Start.Locked && End.Locked;

        public int RetryCount { get; set; }
        public bool WasResetByOverlap { get; set; }
        public string PossibleBleedFromPrev { get; set; }
        public string PossibleBleedFromNext { get; set; }

        public TimingRound CurrentRound { get; set; }
        public List<TimingRound> Rounds { get; set; } = new List<TimingRound>();

        public List<string> ValidationNotes { get; set; } = new List<string>();
        public List<string> Traces { get; set; } = new List<string>();

        public void LockAll()
        {
            Start.Locked = true;
            End.Locked = true;
        }

        public void UnlockAll()
        {
            Start.Locked = false;
            End.Locked = false;
        }

        public void AddValidationNote(string note)
        {
            if (!ValidationNotes.Contains(note))
                ValidationNotes.Add(note);
        }

        public string GetLockTag()
        {
            if (Start.Locked && End.Locked) return "**";
            if (Start.Locked) return "S?";
            if (End.Locked) return "?E";
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
            return new Subtitle(Start.Current, End.Current, new[] { OriginalText });
        }
    }
}
