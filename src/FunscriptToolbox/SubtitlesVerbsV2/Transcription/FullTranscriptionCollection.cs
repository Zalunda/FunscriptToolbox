using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcription
{
    internal class FullTranscriptionCollection : List<FullTranscription>
    {
        public FullTranscriptionCollection()
            : base()
        {
        }

        public void AddIfMissing(string id, Func<string, FullTranscription> action)
        {
            if (!this.Any(t => t.Id == id))
            {
                Add(action(id));
            }
        }
    }
}