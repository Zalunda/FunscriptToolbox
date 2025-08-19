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
            TimeSpan transcriptionDuration,
            int? nbPromptTokens = null,
            int? nbCompletionTokens = null,
            int? nbTotalTokens = null)
            : base(taskName, timeTaken, nbPromptTokens, nbCompletionTokens, nbTotalTokens)
        {
            NbAudios = nbAudios;
            TranscriptionDuration = transcriptionDuration;
        }
    }
}