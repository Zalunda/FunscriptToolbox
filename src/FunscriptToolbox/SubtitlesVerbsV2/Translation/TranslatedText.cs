﻿namespace FunscriptToolbox.SubtitlesVerbsV2.Translation
{
    public class TranslatedText
    {
        public string Id { get; }
        public string Text { get; }
        // TODO Type: Simple, Merged

        public TranslatedText(string id, string text)
        {
            Id = id;
            Text = text;
        }
    }
}