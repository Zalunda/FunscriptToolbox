using System;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class AIMessagesHandlerExpection : Exception
    { 
        public AIMessagesHandlerExpection() 
        { 
        }

        public AIMessagesHandlerExpection(Exception ex, string partiallyFixedResponse)
            : base(ex.Message, ex)
        {
            PartiallyFixedResponse = partiallyFixedResponse;
        }

        public string PartiallyFixedResponse { get; }
    }
}