using System;

namespace FunscriptToolbox.SubtitlesVerbsV2.AudioExtraction
{
    public class AudioNormalizationRule : IEquatable<AudioNormalizationRule>
    {
        public AudioNormalizationRule(TimeSpan startTime, string ffmpegParameters)
        {
            StartTime = startTime;
            FfmpegParameters = ffmpegParameters;
        }

        public TimeSpan StartTime { get; }
        public string FfmpegParameters { get; }

        public bool Equals(AudioNormalizationRule other)
        {
            return this.StartTime == other.StartTime && this.FfmpegParameters == other.FfmpegParameters;
        }
    }
}