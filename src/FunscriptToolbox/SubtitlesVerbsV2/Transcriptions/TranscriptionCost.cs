using System;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public class TranscriptionCost : Cost
    {
        public int NumberOfAudio { get; }
        public TimeSpan TranscriptionDuration { get; }

        public TranscriptionCost(
            string taskName,
            TimeSpan timeTaken,
            int numberOfAudio,
            TimeSpan transcriptionDuration)
            : base(taskName, timeTaken)
        {
            NumberOfAudio = numberOfAudio;
            TranscriptionDuration = transcriptionDuration;
        }
    }
}