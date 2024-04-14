using System;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public class SubtitleForcedTiming
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
    }
}