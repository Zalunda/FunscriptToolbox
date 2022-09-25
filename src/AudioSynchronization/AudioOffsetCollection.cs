using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AudioSynchronization
{
    public class AudioOffsetCollection : ReadOnlyCollection<AudioOffset>
    { 
        public AudioOffsetCollection(IEnumerable<AudioOffset> items)
            : base(items.ToList())
        {
        }

        public TimeSpan? TransformPosition(TimeSpan oldTime)
        {
            foreach (var item in this)
            {
                if (oldTime >= item.Start && oldTime < item.End)
                {
                    return oldTime - item.Offset;
                }
            }

            return null;
        }
    }
}