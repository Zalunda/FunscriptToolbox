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

        public int DefaultActivityFilter { get; set; }
        public int DefaultQualityFilter { get; set; }
        public double DefaultMinimumPercentageFilter { get; set; }
        public int MaximumMemoryUsageInMB { get; set; }
        public bool ShowUI { get; set; }
        public bool TopMostUI { get; set; }

        public double DurationToGenerateInSeconds { get; set; }
        public double MaximumStrokesDetectedPerSecond { get; set; }

        public TimeSpan CurrentVideoTimeAsTimeSpan => TimeSpan.FromMilliseconds(CurrentVideoTime);
        public FrameAnalyser CreateInitialFrameAnalyser(MotionVectorsFileReader mvsReader)
        {
            var learningActions = this.SelectedActions.Length > 0
                ? this.SelectedActions
                : this.Actions.Length > 0
                    ? this.Actions
                    : null;
            return learningActions == null
                ? new FrameAnalyser(mvsReader.FrameLayout)
                : FrameAnalyserGenerator.CreateFromScriptSequence(mvsReader, learningActions);
        }
    }
}

