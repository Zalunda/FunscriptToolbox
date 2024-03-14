using Whisper.net.Ggml;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcription
{
    public class WhisperConfig
    {
        public WhisperConfig(GgmlType modelEnum, int iteration)
        {
            ModelEnum = modelEnum;
            Iteration = iteration;
            ModelName = $"ggml-{modelEnum}.bin";
        }

        public GgmlType ModelEnum { get; }
        public string ModelName { get; }
        public int Iteration { get; }
    }
}