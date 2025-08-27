using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class TranslatedItem : TimedItemWithMetadata
    {
        [JsonConstructor]
        public TranslatedItem(
            TimeSpan startTime,
            TimeSpan endTime,
            Dictionary<string, string> metadata = null)
            : base(startTime, endTime, metadata)
        {
        }
    }
}