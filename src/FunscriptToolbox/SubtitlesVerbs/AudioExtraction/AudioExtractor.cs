using System;

namespace FunscriptToolbox.SubtitlesVerbs.AudioExtraction
{
    public class AudioExtractor
    {
        public string DefaultFfmpegWavParameters { get; set; } = "";

        internal PcmAudio ExtractPcmAudio(SubtitleGeneratorContext context, string inputMp4Fullpath)
        {
            var processStartTime = DateTime.Now;

            var audio = context.FfmpegAudioHelper.ExtractPcmAudio(
                inputMp4Fullpath, 
                this.DefaultFfmpegWavParameters);

            if (context.IsVerbose)
            {
                context.FfmpegAudioHelper.ConvertPcmAudioToWavFile(
                    audio,
                    context.GetPotentialVerboseFilePath("audio-original.wav", processStartTime));
            }

            return audio;
        }
    }
}