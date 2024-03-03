namespace FunscriptToolbox.SubtitlesVerbV2
{
    internal interface IFfmpegAudioHelper
    {
        PcmAudio ExtractPcmAudio(string inputPath, int samplingRate = 16000, string extractionParameters = null);
        public void ConvertPcmAudioToWavFile(PcmAudio pcmAudio, string outputWavFilepath);
   }
}