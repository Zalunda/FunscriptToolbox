using System;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public class AIRequestException : Exception
    { 
        public AIRequestException() 
        { 
        }

        public AIRequestException(AIRequest request, string message, string responseBodyPartiallyFixed = null)
            : base(message)
        {
            Request = request;
            ResponseBodyPartiallyFixed = responseBodyPartiallyFixed;
        }

        public AIRequestException(Exception ex, AIRequest request, string message = null, string responseBodyPartiallyFixed = null)
            : base(message ?? ex.Message, ex)
        {
            Request = request;
            ResponseBodyPartiallyFixed = responseBodyPartiallyFixed;
        }

        public AIRequest Request { get; }
        public string ResponseBodyPartiallyFixed { get; }
    }
}