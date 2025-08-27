using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class TranslatedItem : TimedItemWithMetadata
    {
        public TranscribedItem Source { get; }

        [JsonConstructor]
        public TranslatedItem(
            TranscribedItem source,
            TimeSpan startTime,
            TimeSpan endTime,
            Dictionary<string, string> metadata = null)
            : base(startTime, endTime, metadata)
        {
            this.Source = source;
        }
    }
}