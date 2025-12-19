using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIResponse
    {
        public AIRequest Request { get; }
        public string AssistantMessage { get; }
        public Cost Cost { get; }
        public List<Cost> AdditionalCosts { get; }

        public AIResponse(
            AIRequest request, 
            string assistantMessage,
            Cost cost,
            IEnumerable<Cost> additionalCosts = null)
        {
            Request = request;
            AssistantMessage = assistantMessage;
            Cost = cost;
            AdditionalCosts = additionalCosts?.ToList() ?? new List<Cost>();
        }
    }
}