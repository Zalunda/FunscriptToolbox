using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    public enum DipCandidateState
    {
        Untested,
        Eliminated,
        Selected,
        HardLimit
    }

    public class BoundaryDipCandidate
    {
        public TimeSpan Time { get; set; }
        public DipCandidateState State { get; set; } = DipCandidateState.Untested;

        public BoundaryDipCandidate() { }

        public BoundaryDipCandidate(TimeSpan time)
        {
            Time = time;
        }

        public string Format(TimeSpan? initTime = null)
        {
            string mark = State switch
            {
                DipCandidateState.Selected => "[OK]",
                DipCandidateState.Eliminated => "[x]",
                DipCandidateState.HardLimit => "[H]",
                _ => "[ ]"
            };

            bool isInit = initTime.HasValue && Time == initTime.Value;
            string timeStr = isInit ? $"*{Time:mm\\:ss\\.fff}*" : $"{Time:mm\\:ss\\.fff}";
            return $"{mark} {timeStr}";
        }

        public override string ToString() => Format();
    }
}
