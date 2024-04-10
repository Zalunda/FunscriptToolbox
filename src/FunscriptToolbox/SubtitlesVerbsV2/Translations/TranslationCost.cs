using System;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    public class TranslationCost : Cost
    {
        public int NbTexts { get; }
        public int? NbInputTokens { get; }
        public int? NbOutputTokens { get; }

        public TranslationCost(
            string taskName,
            TimeSpan timeTaken,
            int nbTexts, 
            int? nbInputTokens = null,
            int? nbOutputTokens = null)
            : base(taskName, timeTaken)
        {
            NbTexts = nbTexts;
            NbInputTokens = nbInputTokens;
            NbOutputTokens = nbOutputTokens;
        }
    }
}