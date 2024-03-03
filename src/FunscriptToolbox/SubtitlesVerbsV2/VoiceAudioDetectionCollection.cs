using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    public class VoiceAudioDetectionCollection : ReadOnlyCollection<VoiceAudioDetection>
    {
        public VoiceAudioDetectionCollection(IEnumerable<VoiceAudioDetection> vod)
            : base(vod.ToArray())
        {
        }
    }
}