using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.RetimerVerbs
{
    [JsonObject(IsReference = false)]
    public class SyncOffsetDto
    {
        public TimeSpan OriginalStartTime { get; set; }
        public TimeSpan OriginalEndTime { get; set; }

        public TimeSpan RetimerStartTime { get; set; }
        public TimeSpan RetimerEndTime { get; set; }

        public TimeSpan Offset { get; set; }
    }
}