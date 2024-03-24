using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    public class SubtitleForcedLocationCollection : ReadOnlyCollection<SubtitleForcedLocation>
    {
        public SubtitleForcedLocationCollection(IEnumerable<SubtitleForcedLocation> vod)
            : base(vod.ToArray())
        {
        }
    }
}