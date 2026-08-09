using Newtonsoft.Json;
using System;

namespace FunscriptToolbox.RetimerVerbs
{
    [JsonObject(IsReference = false)]
    public class SyncOffsetDto
    {
        public TimeSpan InputStartTime { get; set; }
        public TimeSpan InputEndTime { get; set; }

        public TimeSpan OutputStartTime { get; set; }
        public TimeSpan OutputEndTime { get; set; }

        public TimeSpan Duration { get; set; }
        public TimeSpan Offset { get; set; }
    }
}