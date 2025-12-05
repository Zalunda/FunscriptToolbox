using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIResponseException : Exception
    { 
        public AIResponseException() 
        { 
        }

        public AIResponseException(AIRequest request, string message, string responseBody = null, string responseBodyPartiallyFixed = null)
            : base(message)
        {
            Request = request;
            ResponseBody = responseBody;
            ResponseBodyPartiallyFixed = responseBodyPartiallyFixed;
        }

        public AIResponseException(Exception ex, AIRequest request, string message, string responseBody, string responseBodyPartiallyFixed)
            : base(message, ex)
        {
            Request = request;
            ResponseBody = responseBody;
            ResponseBodyPartiallyFixed = responseBodyPartiallyFixed;
        }

        public AIRequest Request { get; }
        public string ResponseBody { get; }
        public string ResponseBodyPartiallyFixed { get; }
    }
}