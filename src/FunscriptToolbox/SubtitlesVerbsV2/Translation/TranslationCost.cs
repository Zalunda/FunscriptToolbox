using System;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    public class TranslationCost
    {
        public string TaskName { get; }
        public int NbTexts { get; }
        public TimeSpan TimeTaken { get; }
        public int? NbInputTokens { get; }
        public int? NbOutputTokens { get; }

        public TranslationCost(
            string taskName, 
            int nbTexts, 
            TimeSpan timeTaken, 
            int? nbInputTokens = null,
            int? nbOutputTokens = null)
        {
            TaskName = taskName;
            NbTexts = nbTexts;
            TimeTaken = timeTaken;
            NbInputTokens = nbInputTokens;
            NbOutputTokens = nbOutputTokens;
        }
    }
}