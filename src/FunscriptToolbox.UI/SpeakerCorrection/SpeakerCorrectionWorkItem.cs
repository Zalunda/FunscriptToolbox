using System;
using System.Collections.Generic;

namespace FunscriptToolbox.UI.SpeakerCorrection
{
    public class SpeakerCorrectionWorkItem
    {
        public TimeSpan StartTime { get; }
        public TimeSpan EndTime { get; }
        public List<string> PotentialSpeakers { get; set; }
        public string DetectedSpeaker { get; set; }
        public string FinalSpeaker { get; set; }

        public SpeakerCorrectionWorkItem(
            TimeSpan startTime, 
            TimeSpan endTime,
            IEnumerable<string> potentialSpeakers,
            string detectedSpeaker)
        {
            this.StartTime = startTime;
            this.EndTime = endTime;
            this.PotentialSpeakers = new List<string>(potentialSpeakers ?? Array.Empty<string>());
            this.DetectedSpeaker = detectedSpeaker;
        }
    }
}