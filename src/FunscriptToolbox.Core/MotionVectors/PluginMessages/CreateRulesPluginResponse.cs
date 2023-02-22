using FunscriptToolbox.Core;

namespace FunscriptToolbox.Core.MotionVectors.PluginMessages
{
    public class CreateRulesPluginResponse : PluginResponse
    {
        public double FrameDurationInMs { get; set; }
        public FunscriptAction[] Actions { get; set; }
    }
}

