using System;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
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