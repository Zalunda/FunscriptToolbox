using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.AudioSyncVerbs
{
    internal class VirtualMergedAudioOffset
    {
        [JsonIgnore]
        public AudioSignatureWithLinkedFiles InputFile { get; set; }

        [JsonProperty("InputFile")]
        private string InputFilePath => InputFile?.FullPath;

        public TimeSpan InputStartTime { get; set; }

        [JsonIgnore]
        public AudioSignatureWithLinkedFiles OutputFile { get; set; }

        [JsonProperty("OutputFile")]
        private string OutputFilePath => OutputFile?.FullPath;

        public TimeSpan? OutputStartTime { get; set; }
        public TimeSpan Duration { get; set; }
        public TimeSpan? Offset { get; set; }

        [JsonIgnore]
        public int NbTimesUsed { get; set; }
    }
}
