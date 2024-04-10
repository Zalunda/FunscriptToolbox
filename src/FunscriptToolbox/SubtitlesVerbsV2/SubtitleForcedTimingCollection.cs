using FunscriptToolbox.SubtitlesVerbsV2.AudioExtraction;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbsV2
{
    public class SubtitleForcedTimingCollection : ReadOnlyCollection<SubtitleForcedTiming>
    {
        public SubtitleForcedTimingCollection(IEnumerable<SubtitleForcedTiming> vod)
            : base(vod.ToArray())
        {
        }

        internal string GetContextAt(TimeSpan startTime)
        {
            return this
                .LastOrDefault(f => f.StartTime < startTime && f.ContextText != null)?.ContextText;
        }

        internal string GetTalkerAt(TimeSpan startTime, TimeSpan endTime)
        {
            return this
                .Select(sft => {
                    var percentage = (int)(100 * (Math.Min(endTime.TotalMilliseconds, sft.EndTime.TotalMilliseconds)
                        - Math.Max(startTime.TotalMilliseconds, sft.StartTime.TotalMilliseconds)) / sft.Duration.TotalMilliseconds);
                    return new { percentage, sft }; 
                })
                .OrderByDescending(item => item.percentage)
                .FirstOrDefault()
                ?.sft.Talker;
        }

        internal AudioNormalizationRule[] GetAudioNormalizationRules()
        {
            return this
                .Items
                .Where(item => item.AudioNormalizationParameters != null)
                .Select(item => new AudioNormalizationRule(item.StartTime, item.AudioNormalizationParameters))
                .ToArray();
        }
    }
}