﻿namespace FunscriptToolbox.Core.MotionVectors.PluginMessages
{
    public class SharedConfig
    {
        public int MaximumMemoryUsageInMB { get; set; }
        public double DefaultLearningDurationInSeconds { get; set; }
        public int DefaultActivityFilter { get; set; }
        public int DefaultQualityFilter { get; set; }
        public double DefaultMinimumPercentageFilter { get; set; }
        public double MaximumNbStrokesDetectedPerSecond { get; set; }
        public double MaximumLearningDurationInSeconds { get; set; }
        public bool TopMostUI { get; set; }
    }
}

