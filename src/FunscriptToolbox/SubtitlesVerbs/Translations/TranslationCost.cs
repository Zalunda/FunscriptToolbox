using System;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class TranslationCost : Cost
    {
        public int NbTexts { get; }

        public TranslationCost(
            string taskName,
            TimeSpan timeTaken,
            int nbTexts,
            int? nbPromptTokens = null,
            int? nbCompletionTokens = null,
            int? nbTotalTokens = null)
            : base(taskName, timeTaken, nbPromptTokens, nbCompletionTokens, nbTotalTokens)
        {
            NbTexts = nbTexts;
        }
    }
}