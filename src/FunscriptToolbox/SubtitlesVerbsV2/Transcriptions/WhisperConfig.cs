namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public abstract class WhisperConfig
    {
        public string ApplicationFullPath { get; set; }
        public string AdditionalParameters { get; set; } = "";
    }
}