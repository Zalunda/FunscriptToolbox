using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using FunscriptToolbox.SubtitlesVerbV2;
using System;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    public abstract class Translator
    {
        public Translator(
            string translationId)
        {
            TranslationId = translationId;
        }

        public string TranslationId { get; }
        public Language TargetLanguage { get; set; } = Language.FromString("en");

        public abstract void Translate(
            string baseFilePath,
            Transcription transcription,
            Translation translation,
            Action saveAction);
    }

}