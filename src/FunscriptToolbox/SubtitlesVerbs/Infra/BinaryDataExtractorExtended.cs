using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class BinaryDataExtractorExtended
    {
        public BinaryDataExtractor Extractor { get; set; }
        public AIRequestPart[] TrainingContentLists { get; set; }
        public Func<AIRequestSection, ITiming, string, bool, AIRequestPart[]> GetData { get; set; }

        public (TimeSpan time, string name, BinaryDataType dataType, AIRequestPart[] contentList)[] GetContextOnlyNodes(AIRequestSection section, ITiming gap, Func<TimeSpan, string> getText, bool silent = false)
        {
            (TimeSpan, string, BinaryDataType, AIRequestPart[]) CreateContextNode(TimeSpan time)
            {
                return (time, this.Extractor.OutputFieldName, this.Extractor.DataType, this.GetData(section, new Timing(time, time), getText(time), silent));
            }

            var contextNodes = new List<(TimeSpan, string, BinaryDataType, AIRequestPart[])>();
            foreach (var time in this.Extractor.GetContextTimes(gap))
            {
                contextNodes.Add(CreateContextNode(time));
            }
            return contextNodes.ToArray();
        }
    }
}