using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.Core;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    public class TimingCandidate
    {
        public TimeSpan OriginalStartTime { get; set; }
        public TimeSpan OriginalEndTime { get; set; }
        public string OriginalText { get; set; }

        [JsonIgnore] public TimedItemWithMetadata OriginalItem { get; set; }

        // Initial snapped boundaries
        public TimeSpan InitStart { get; set; }
        public TimeSpan InitEnd { get; set; }

        // Current best / working boundaries
        public TimeSpan CurrentTestStart { get; set; }
        public TimeSpan CurrentTestEnd { get; set; }

        public TimeSpan? HardLimitStart { get; set; }
        public TimeSpan? HardLimitEnd { get; set; }

        public bool StartLocked { get; set; }
        public bool EndLocked { get; set; }
        [JsonIgnore] public bool IsFullyLocked => StartLocked && EndLocked;

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
            StartLocked = true;
            EndLocked = true;
        }

        public void AddValidationNote(string note)
        {
            if (!ValidationNotes.Contains(note))
                ValidationNotes.Add(note);
        }

        public string GetLockTag()
        {
            if (StartLocked && EndLocked) return "**";
            if (StartLocked) return "S?";
            if (EndLocked) return "?E";
            return "??";
        }

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
            return new Subtitle(this.CurrentTestStart, this.CurrentTestEnd, new[] { this.OriginalText });
        }
    }
}
