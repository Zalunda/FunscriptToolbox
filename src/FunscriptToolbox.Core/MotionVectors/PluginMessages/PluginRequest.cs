using System;

namespace FunscriptToolbox.Core.MotionVectors.PluginMessages
{
    public class PluginRequest
    {
        public string VideoFullPath { get; set; }
        public int CurrentVideoTime { get; set; }
        public string MvsFullPath { get; set; }
        public SharedConfig SharedConfig { get; set; }

        public TimeSpan CurrentVideoTimeAsTimeSpan => TimeSpan.FromMilliseconds(CurrentVideoTime);
    }
}

