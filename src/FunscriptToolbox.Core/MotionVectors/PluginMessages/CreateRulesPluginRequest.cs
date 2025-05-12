using System;

namespace FunscriptToolbox.Core.MotionVectors.PluginMessages
{
    public class CreateRulesPluginRequest : PluginRequest
    {
        public string VideoFullPath { get; set; }
        public int CurrentVideoTime { get; set; }
        public string MvsFullPath { get; set; }
        public int ScriptIndex { get; set; }
        public FunscriptAction[] Actions { get; set; }
        public FunscriptAction[] SelectedActions { get; set; }

        public int LearnFromAction_NbFramesToIgnoreAroundAction { get; set; }
        public int LearnFromAction_DefaultActivityFilter { get; set; }
        public int LearnFromAction_DefaultQualityFilter { get; set; }
        public double LearnFromAction_DefaultMinimumPercentageFilter { get; set; }
        public bool LearnFromAction_ShowUI { get; set; }
        public bool LearnFromAction_TopMostUI { get; set; }

        public double GenerateActions_MaximumStrokesDetectedPerSecond { get; set; }
        public int GenerateActions_PercentageOfFramesToKeep { get; set; }
        public double GenerateActions_DurationToGenerateInSeconds { get; set; }

        public int MaximumMemoryUsageInMB { get; set; }

        public TimeSpan CurrentVideoTimeAsTimeSpan => TimeSpan.FromMilliseconds(CurrentVideoTime);
        public FrameAnalyser CreateInitialFrameAnalyser(MotionVectorsFileReader mvsReader)
        {
            var learningActions = this.SelectedActions.Length > 0
                ? this.SelectedActions
                : this.Actions.Length > 1
                    ? this.Actions
                    : null; // TODO Automatic
            return learningActions == null
                ? new FrameAnalyser(mvsReader.FrameLayout)
                : FrameAnalyserGenerator.CreateFromScriptSequence(
                    mvsReader, 
                    learningActions,
                    new LearnFromActionsSettings { 
                        NbFramesToIgnoreAroundAction = this.LearnFromAction_NbFramesToIgnoreAroundAction });
        }
    }
}

