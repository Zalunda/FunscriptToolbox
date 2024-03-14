using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcription
{
    internal class FullTranscription
    {
        public string Id { get; }

        public TranscribedText[] Items { get; }

        public FullTranscription(string id, IEnumerable<TranscribedText> items)
        {
            Id = id;
            Items = items.ToArray();
        }
    }
}