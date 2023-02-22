using System;
using System.Linq;

namespace FunscriptToolbox.Core.MotionVectors.PluginMessages
{

    public class CreateRulesPluginRequest : PluginRequest
    {
        public FunscriptAction[] Actions { get; set; }
        public FunscriptAction[] SelectedActions { get; set; }
        public double DurationToGenerateInSeconds { get; set; }
        public bool ShowUI { get; set; }

        public FrameAnalyser CreateInitialFrameAnalyser(MotionVectorsFileReader mvsReader)
        {
            var learningActions = this.SelectedActions.Length > 0
                ? this.SelectedActions
                : this.Actions.Length > 0
                    ? this.Actions
                    : null;
            return learningActions == null
                ? new FrameAnalyser(mvsReader.NbBlocX, mvsReader.NbBlocY)
                : FrameAnalyserGenerator.CreateFromScriptSequence(mvsReader, learningActions);
        }
    }
}

