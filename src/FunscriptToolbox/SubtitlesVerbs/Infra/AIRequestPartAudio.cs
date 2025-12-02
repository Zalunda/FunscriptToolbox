namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class AIRequestPartAudio : AIRequestPart
    {
        public AIRequestPartAudio(string filename, byte[] content)
        {
            this.FileName = filename;
            this.Content = content;
        }

        public string FileName { get; }
        public byte[] Content { get; }

        public override string ForSimplifiedFullPrompt() => "[Audio]";
    }
}