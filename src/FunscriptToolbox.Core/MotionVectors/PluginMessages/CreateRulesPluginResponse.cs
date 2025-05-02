namespace FunscriptToolbox.Core.MotionVectors.PluginMessages
{
    public class CreateRulesPluginResponse : PluginResponse
    {
        public double FrameDurationInMs { get; set; }
        public int CurrentVideoTime { get; set; }
        public int ScriptIndex { get; set; }
        public FunscriptActionExtended[] Actions { get; set; }
    }
}

