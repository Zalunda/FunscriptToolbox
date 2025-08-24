using System;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbs
{
    public class MetadataCollection : Dictionary<string, string>
    {
        public MetadataCollection()
            : base(StringComparer.OrdinalIgnoreCase)
        {
        }
        public MetadataCollection(Dictionary<string, string> items)
            : base(items, StringComparer.OrdinalIgnoreCase)
        {
        }


        // --- Business Logic Shortcuts ---
        public bool IsVoice => this.OnScreenText == null && !this.ContainsKey("NoVoice");
        public string VoiceText => this.Get("VoiceText");
        public string GrabOnScreenText => this.Get("GrabOnScreenText");
        public string OnScreenText => this.Get("OnScreenText");
        public object SpeakerTraining => this.Get("SpeakerTraining");

        // --- Generic Accessors ---
        public string Get(string key) { this.TryGetValue(key, out var v); return v; }

        public void Merge(MetadataCollection other)
        {
            foreach (var kv in other)
            {
                this[kv.Key] = kv.Value;
            }
        }
    }
}