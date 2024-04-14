using System;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public abstract class Cost
    {
        public string TaskName { get; }
        public TimeSpan TimeTaken { get; }

        protected Cost(string taskName, TimeSpan timeTaken)
        {
            this.TaskName = taskName;
            this.TimeTaken = timeTaken;
        }
    }
}