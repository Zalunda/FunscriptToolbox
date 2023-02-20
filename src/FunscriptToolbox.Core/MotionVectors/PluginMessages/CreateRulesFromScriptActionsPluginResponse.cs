using FunscriptToolbox.Core;

namespace FunscriptToolbox.Core.MotionVectors.PluginMessages
{
    public class CreateRulesFromScriptActionsPluginResponse
    {
        public double FrameDurationInMs { get; set; }
        public FunscriptAction[] Actions { get; set; }
    }
}

