using FunscriptToolbox.Core;

namespace FunscriptToolbox.MotionVectorsVerbs.PluginMessages
{
    internal class CreateRulesFromScriptActionsPluginRequest : PluginRequest
    {
        public FunscriptAction[] Actions { get; set; }
        public int DurationToGenerateInSeconds { get; set; }
        public double MaximumNbStrokesDetectedPerSecond { get; set; }
        public bool ShowUI { get; set; }

        public class Response
        {
            public FunscriptAction[] Actions { get; set; }
        }
    }
}

