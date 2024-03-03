using Whisper.net.Ggml;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    public class WhisperConfig
    {
        public WhisperConfig(GgmlType modelEnum, int iteration)
        {
            this.ModelEnum = modelEnum;
            this.Iteration = iteration;
            this.ModelName = $"ggml-{modelEnum}.bin";
        }

        public GgmlType ModelEnum { get; }
        public string ModelName { get; }
        public int Iteration { get; }
    }
}