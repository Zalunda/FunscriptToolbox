namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIResponse
    {
        public AIRequest Request { get; }
        public string AssistantMessage { get; }
        public Cost DraftOfCost { get; }

        public AIResponse(
            AIRequest request,
            string assistantMessage = null,
            Cost draftOfCost = null) 
        {
            Request = request;
            AssistantMessage = assistantMessage;
            DraftOfCost = draftOfCost;
        }
    }
}