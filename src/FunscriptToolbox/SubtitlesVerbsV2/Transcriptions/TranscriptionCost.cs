using System;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public class TranscriptionCost
    {
        public string TaskName { get; }
        public int NumberOfAudio { get; }
        public TimeSpan TranscriptionDuration { get; }
        public TimeSpan TimeTaken { get; }
        public string ExecutionLogs { get; }

        public TranscriptionCost(
            string taskName, 
            int numberOfAudio,
            TimeSpan transcriptionDuration, 
            TimeSpan timeTaken,
            string executionLogs)
        {
            TaskName = taskName;
            NumberOfAudio = numberOfAudio;
            TranscriptionDuration = transcriptionDuration;
            TimeTaken = timeTaken;
            ExecutionLogs = executionLogs;
        }
    }
}