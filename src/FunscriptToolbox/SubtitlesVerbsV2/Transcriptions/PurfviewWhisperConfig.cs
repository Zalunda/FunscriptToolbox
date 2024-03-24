using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public class PurfviewWhisperConfig : WhisperConfig
    {
        public string Model { get; set; } = "Large-V2";
        public bool ForceSplitOnComma { get; set; } = true;
        public TimeSpan RedoBlockLargerThen { get; set; } = TimeSpan.FromSeconds(15);
    }
}