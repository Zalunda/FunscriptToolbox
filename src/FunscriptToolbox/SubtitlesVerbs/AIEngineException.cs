using System;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public class AIEngineException : Exception
    { 
        public AIEngineException() 
        { 
        }

        public AIEngineException(Exception ex, string partiallyFixedResponse)
            : base(ex.Message, ex)
        {
            PartiallyFixedResponse = partiallyFixedResponse;
        }

        public string PartiallyFixedResponse { get; }
    }
}