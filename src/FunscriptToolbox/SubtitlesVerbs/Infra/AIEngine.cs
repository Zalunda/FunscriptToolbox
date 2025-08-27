namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class AIEngine
    {
        public abstract AIResponse Execute(
            SubtitleGeneratorContext context,
            AIRequest request);
    }
}