using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbV2
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
                .LastOrDefault(f => f.StartTime < startTime && f.Type == SubtitleForcedTimingType.Context)?.Text;
        }
    }
}