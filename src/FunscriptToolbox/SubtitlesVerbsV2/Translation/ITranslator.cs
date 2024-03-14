using FunscriptToolbox.SubtitlesVerbsV2.Transcription;
using System;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translation
{
    internal interface ITranslator
    {
        void Translate(
            string translationId, 
            FullTranscription transcription, 
            string sourceLanguage, 
            string targetLanguage,
            Action saveAction);
    }
}