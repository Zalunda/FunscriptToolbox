using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class BinaryDataExtractorExtended
    {
        public BinaryDataExtractor Extractor { get; set; }
        public dynamic[] TrainingContentLists { get; set; }
        public Func<ITiming, string, dynamic[]> GetData { get; set; }

        public (TimeSpan time, string name, dynamic[] contentList)[] GetContextOnlyNodes(ITiming gap, Func<TimeSpan, string> getText)
        {
            (TimeSpan, string, dynamic[]) CreateContextNode(TimeSpan time)
            {
                return (time, this.Extractor.OutputFieldName, this.GetData(new Timing(time, time), getText(time)));
            }

            if (!this.Extractor.AddContextNodes)
            {
                return Array.Empty<(TimeSpan, string, dynamic[])>();
            }

            var shortGapConfig = this.Extractor.ContextShortGap == TimeSpan.Zero 
                ? this.Extractor.ContextLongGap
                : this.Extractor.ContextShortGap;
            var longGapConfig = this.Extractor.ContextLongGap;

            if (gap.Duration < TimeSpan.Zero)
            {
                return new[] { CreateContextNode(gap.StartTime + shortGapConfig) };
            }

            if (gap.Duration > shortGapConfig)
            {
                var contextNodes = new List<(TimeSpan, string, dynamic[])>();
                if (gap.Duration <= shortGapConfig + shortGapConfig + shortGapConfig)
                {
                    var middleTime = gap.StartTime + TimeSpan.FromMilliseconds(gap.Duration.TotalMilliseconds / 2);
                    contextNodes.Add(CreateContextNode(middleTime));
                }
                else
                {
                    var firstContextNodeTime = gap.StartTime + shortGapConfig;
                    var lastContextNodeTime = gap.EndTime - shortGapConfig;
                    contextNodes.Add(CreateContextNode(firstContextNodeTime));

                    var remainingGrapDuration = lastContextNodeTime - firstContextNodeTime;
                    if (remainingGrapDuration > longGapConfig)
                    {
                        var currentTime = firstContextNodeTime;
                        var nbSections = Math.Floor(remainingGrapDuration.TotalMilliseconds / longGapConfig.TotalMilliseconds) + 1;
                        var sectionDuration = TimeSpan.FromMilliseconds(remainingGrapDuration.TotalMilliseconds / nbSections);
                        for (var i = 0; i < nbSections - 1; i++)
                        {
                            currentTime += sectionDuration;
                            contextNodes.Add(CreateContextNode(currentTime));
                        }
                    }

                    contextNodes.Add(CreateContextNode(lastContextNodeTime));
                }

                return contextNodes.ToArray();
            }
            else
            {
                return Array.Empty<(TimeSpan, string, dynamic[])>();
            }
        }
    }
}