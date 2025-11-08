using System;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class BinaryDataExtractorExtended
    {
        public string OutputFieldName { get; set; }
        public BinaryDataType DataType { get; set; }
        public dynamic[] TrainingContentLists { get; set; }
        public Func<ITiming, string, dynamic[]> GetData { get; set; }
    }
}