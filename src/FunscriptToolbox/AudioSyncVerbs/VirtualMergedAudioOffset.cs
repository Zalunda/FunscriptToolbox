using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.AudioSyncVerbs
{
    internal enum ItemType
    {
        Actions,
        Chapters,
        Subtitles
    }

    internal class VirtualMergedAudioOffset
    {
        [JsonIgnore]
        public AudioSignatureWithLinkedFiles InputFile { get; set; }

        [JsonProperty("InputFile")]
        private string InputFilePath => InputFile?.FullPath;

        public TimeSpan? InputStartTime { get; set; }

        [JsonIgnore]
        public AudioSignatureWithLinkedFiles OutputFile { get; set; }

        [JsonProperty("OutputFile")]
        private string OutputFilePath => OutputFile?.FullPath;

        public TimeSpan? OutputStartTime { get; set; }
        public TimeSpan Duration { get; set; }
        public TimeSpan? Offset { get; set; }

        public Dictionary<ItemType, int> Usage { get; } = Enum
            .GetValues(typeof(ItemType))
            .Cast<ItemType>()
            .ToDictionary(type => type, _ => 0);
    }
}
