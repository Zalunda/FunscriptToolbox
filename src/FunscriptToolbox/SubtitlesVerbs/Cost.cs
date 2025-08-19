using System;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public abstract class Cost
    {
        public string TaskName { get; }
        public TimeSpan TimeTaken { get; }

        public int? NbPromptTokens { get; }
        public int? NbCompletionTokens { get; }
        public int? NbTotalTokens { get; }

        protected Cost(
            string taskName, 
            TimeSpan timeTaken, 
            int? nbPromptTokens = null, 
            int? nbCompletionTokens = null, 
            int? nbTotalTokens = null)
        {
            this.TaskName = taskName;
            this.TimeTaken = timeTaken;
            this.NbPromptTokens = nbPromptTokens;
            this.NbCompletionTokens = nbCompletionTokens;
            this.NbTotalTokens = nbTotalTokens;
        }
    }
}