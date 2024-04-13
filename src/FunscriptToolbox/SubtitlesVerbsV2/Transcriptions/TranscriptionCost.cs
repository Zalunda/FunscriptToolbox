using System;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public class TranscriptionCost : Cost
    {
        public int NbAudios { get; }
        public TimeSpan TranscriptionDuration { get; }

        public TranscriptionCost(
            string taskName,
            TimeSpan timeTaken,
            int nbAudios,
            TimeSpan transcriptionDuration)
            : base(taskName, timeTaken)
        {
            NbAudios = nbAudios;
            TranscriptionDuration = transcriptionDuration;
        }
    }
}