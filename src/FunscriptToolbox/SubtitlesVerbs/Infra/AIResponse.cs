namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIResponse
    {
        public AIRequest Request { get; }
        public string AssistantMessage { get; }
        public Cost Cost { get; }

        public AIResponse(
            AIRequest request, 
            string assistantMessage, 
            Cost cost)
        {
            Request = request;
            AssistantMessage = assistantMessage;
            Cost = cost;
        }
    }
}