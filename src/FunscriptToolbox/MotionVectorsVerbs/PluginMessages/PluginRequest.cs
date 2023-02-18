using System;

namespace FunscriptToolbox.MotionVectorsVerbs.PluginMessages
{
    internal class PluginRequest
    {
        public string VideoFullPath { get; set; }
        public int CurrentVideoTime { get; set; }
        public string MvsFullPath { get; set; }
        public int MaximumMemoryUsageInMB { get; set; }

        public TimeSpan CurrentVideoTimeAsTimeSpan => TimeSpan.FromMilliseconds(CurrentVideoTime);
    }
}

