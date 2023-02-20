namespace FunscriptToolbox.Core.MotionVectors.PluginMessages
{

    public class CreateRulesFromScriptActionsPluginRequest : PluginRequest
    {
        public FunscriptAction[] Actions { get; set; }
        public FunscriptAction[] SelectedActions { get; set; }
        public double DurationToGenerateInSeconds { get; set; }
        public bool ShowUI { get; set; }
    }
}

