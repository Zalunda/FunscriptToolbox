using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class TranslatedText : TimedObjectWithMetadata, ITiming
    {
        public TranscribedText Source { get; }
        public string Text => this.Metadata.Get("TranslatedText");

        [JsonConstructor]
        public TranslatedText(
            TranscribedText source,
            TimeSpan startTime,
            TimeSpan endTime,
            string text,
            Dictionary<string, string> metadata = null)
            : base(startTime, endTime, metadata)
        {
            this.Source = source;
            if (!this.Metadata.ContainsKey("TranslatedText") && text != null)
            {
                this.Metadata["TranslatedText"] = text;
            }
        }

        public TranslatedText(
            TranscribedText source, 
            string text)
            : this(source, source.StartTime, source.EndTime, text)
        {
        }
    }
}