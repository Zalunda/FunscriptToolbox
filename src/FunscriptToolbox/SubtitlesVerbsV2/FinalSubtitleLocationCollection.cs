using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    public class FinalSubtitleLocationCollection : ReadOnlyCollection<FinalSubtitleLocation>
    {
        public FinalSubtitleLocationCollection(IEnumerable<FinalSubtitleLocation> vod)
            : base(vod.ToArray())
        {
        }
    }
}