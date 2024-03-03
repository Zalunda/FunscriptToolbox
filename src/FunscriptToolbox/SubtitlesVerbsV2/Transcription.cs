using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    internal class Transcription : ReadOnlyCollection<TranscribedText>
    {
        public Transcription(IEnumerable<TranscribedText> texts)
            : base(texts.ToArray())
        {
        }
    }
}