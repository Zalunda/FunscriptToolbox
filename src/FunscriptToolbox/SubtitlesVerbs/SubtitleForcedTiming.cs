using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public class SubtitleForcedTiming : ITiming
    {
        public TimeSpan StartTime { get; }
        public TimeSpan EndTime { get; }
        public TimeSpan Duration => EndTime - StartTime;
        public string VoiceText { get; }
        public string Talker { get; }
        public string ContextText { get; }
        public string ScreengrabText { get; }
        public string AudioNormalizationParameters { get; }

        public SubtitleForcedTiming(
            TimeSpan startTime, 
            TimeSpan endTime,
            string voiceText,
            string talker = null,
            string contextText = null,
            string screengrabText = null,
            string audioNormalizationConfig = null)
        {
            StartTime = startTime;
            EndTime = endTime;
            VoiceText = voiceText;
            Talker = talker;
            ContextText = contextText;
            ScreengrabText = screengrabText;
            AudioNormalizationParameters = audioNormalizationConfig;
        }

        public override bool Equals(object obj)
        {
            return obj is SubtitleForcedTiming timing &&
                   StartTime.Equals(timing.StartTime) &&
                   EndTime.Equals(timing.EndTime) &&
                   Duration.Equals(timing.Duration) &&
                   VoiceText == timing.VoiceText &&
                   Talker == timing.Talker &&
                   ContextText == timing.ContextText &&
                   ScreengrabText == timing.ScreengrabText &&
                   AudioNormalizationParameters == timing.AudioNormalizationParameters;
        }

        public override int GetHashCode()
        {
            int hashCode = -985589543;
            hashCode = hashCode * -1521134295 + StartTime.GetHashCode();
            hashCode = hashCode * -1521134295 + EndTime.GetHashCode();
            hashCode = hashCode * -1521134295 + Duration.GetHashCode();
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(VoiceText);
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(Talker);
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(ContextText);
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(ScreengrabText);
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(AudioNormalizationParameters);
            return hashCode;
        }
    }
}