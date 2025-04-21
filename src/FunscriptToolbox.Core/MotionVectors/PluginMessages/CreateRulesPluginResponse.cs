namespace FunscriptToolbox.Core.MotionVectors.PluginMessages
{
    public class CreateRulesPluginResponse : PluginResponse
    {
        public double FrameDurationInMs { get; set; }
        public FunscriptActionExtended[] Actions { get; set; }
    }
}

