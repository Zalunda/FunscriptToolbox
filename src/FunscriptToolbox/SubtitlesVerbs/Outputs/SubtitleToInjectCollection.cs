using System.Collections.Generic;

namespace FunscriptToolbox.Core
{
    public class SubtitleToInjectCollection : List<SubtitleToInject>
    {
        public SubtitleToInjectCollection(IEnumerable<SubtitleToInject> subtitles)
        : base(subtitles)
        {
        }
    }
}
