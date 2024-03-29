﻿using System;
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

        public void ResetUsages()
        {
            foreach (var item in this)
            {
                item.ResetUsage();
            }
        }

        public TimeSpan? TransformPosition(TimeSpan oldTime)
        {
            foreach (var item in this)
            {
                if (oldTime >= item.StartTime && oldTime < item.EndTime)
                {
                    item.IncrementUsage();
                    return item.Offset == null 
                        ? null 
                        : oldTime + item.Offset;
                }
            }

            return null;
        }
    }
}