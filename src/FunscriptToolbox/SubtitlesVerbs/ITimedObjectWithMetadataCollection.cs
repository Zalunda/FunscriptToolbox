using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public interface ITimedObjectWithMetadataCollection
    {
        public ICollection<TimedObjectWithMetadata> Items { get; }
    }
}