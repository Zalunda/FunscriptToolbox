using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    internal class TranscriptionCollection : Dictionary<string, Transcription>
    {
        public TranscriptionCollection()
            : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        public void AddIfMissing(string key, Func<Transcription> action)
        {
            if (!ContainsKey(key))
            {
                this[key] = action();
            }
        }
    }
}