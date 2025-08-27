using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class Cost
    {
        public string TaskName { get; }
        public TimeSpan TimeTaken { get; }
        public int NbItems { get; }
        public int NbPromptCharacters { get; }
        public int NbCompletionCharacters { get; }

        public int? NbPromptTokens { get; }
        public int? NbCompletionTokens { get; }
        public int? NbTotalTokens { get; }
        public TimeSpan? ItemsDuration { get; }

        public Cost(
            string taskName, 
            TimeSpan timeTaken,
            int NbItems,
            int nbPromptCharacters = 0,
            int nbCompletionCharacters = 0,
            int? nbPromptTokens = null, 
            int? nbCompletionTokens = null, 
            int? nbTotalTokens = null,
            TimeSpan? itemsDuration = null)
        {
            this.TaskName = taskName;
            this.TimeTaken = timeTaken;
            this.NbItems = NbItems;
            this.NbPromptCharacters = nbPromptCharacters;
            this.NbCompletionCharacters = nbCompletionCharacters;
            this.NbPromptTokens = nbPromptTokens;
            this.NbCompletionTokens = nbCompletionTokens;
            this.NbTotalTokens = nbTotalTokens;
            ItemsDuration = itemsDuration;
        }
    }
}