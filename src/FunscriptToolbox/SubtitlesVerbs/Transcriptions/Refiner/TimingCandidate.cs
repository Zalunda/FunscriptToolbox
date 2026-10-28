using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    public class TimingCandidate
    {
        public TimeSpan OriginalStartTime { get; set; }
        public TimeSpan OriginalEndTime { get; set; }
        public string OriginalText { get; set; }

        [JsonIgnore] public TimedItemWithMetadata OriginalItem { get; set; }

        // Terrain level under which a mountain is considered noise for this subtitle.
        public float SignificantLevel { get; set; }

        // Best window found so far. Once the candidate is finished, this is the final timing.
        public TimeSpan CurrentTestStart { get; set; }
        public TimeSpan CurrentTestEnd { get; set; }

        // Window waiting to be sent to the AI in the next iteration.
        public TimeWindow Plan { get; set; } = null;

        // Every window already tested, with what the AI heard. Nothing is ever discarded.
        public List<TestedWindow> Tests { get; set; } = new List<TestedWindow>();

        public SideSearch Start { get; set; } = new SideSearch();
        public SideSearch End { get; set; } = new SideSearch();

        public SideSearch Side(BoundarySide side) => side == BoundarySide.Start ? Start : End;
        [JsonIgnore] public bool IsFullyLocked => Start.IsLocked && End.IsLocked;

        public int RetryCount { get; set; }
        public List<string> ValidationNotes { get; set; } = new List<string>();
        public List<string> Trace { get; set; } = new List<string>();

        public TestedWindow FindTest(TimeSpan start, TimeSpan end)
        {
            return Tests.FirstOrDefault(t => t.Start == start && t.End == end);
        }

        public void LockAll()
        {
            Start.IsLocked = true;
            End.IsLocked = true;
        }

        public void AddValidationNote(string note)
        {
            if (!ValidationNotes.Contains(note))
                ValidationNotes.Add(note);
        }

        public string GetLockTag()
        {
            if (Start.IsLocked && End.IsLocked) return "**";
            if (Start.IsLocked) return "S?";
            if (End.IsLocked) return "?E";
            return "??";
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

        public void AddTraceRow(string step, char startMod, TimeSpan start, TimeSpan end, char endMod, string result = null, string text = null)
        {
            string lockTag = GetLockTag();
            string stepCol = (step ?? "").PadRight(4);
            string boundsCol = $"[{startMod}{start:hh\\:mm\\:ss\\.fff}=>{end:hh\\:mm\\:ss\\.fff}{endMod}]";

            if (string.IsNullOrEmpty(result) && string.IsNullOrEmpty(text))
            {
                Trace.Add($"{lockTag} {stepCol} {boundsCol}");
            }
            else
            {
                string resCol = (result ?? "").PadRight(24);
                string textCol = text != null ? $"'{text}'" : "";
                Trace.Add($"{lockTag} {stepCol} {boundsCol} {resCol} {textCol}".TrimEnd());
            }
        }
    }
}
