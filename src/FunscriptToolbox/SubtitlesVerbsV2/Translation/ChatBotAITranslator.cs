using FunscriptToolbox.SubtitlesVerbsV2.Transcriptions;
using System;
using System.IO;

namespace FunscriptToolbox.SubtitlesVerbsV2.Translations
{
    internal class ChatBotAITranslator : AITranslator
    {
        public ChatBotAITranslator(
            string translationId)
            : base(translationId)
        {
        }

        public override void Translate(
            string baseFilePath,
            Transcription transcription,
            Translation translation,
            Action saveAction)
        {
            var items = GetAllItems(transcription);
            foreach (var request in CreateRequests(
                items,
                transcription.Language,
                translation.Language))
            {
                File.WriteAllText(
                    $"{baseFilePath}.{transcription.Id}-{translation.Id}-REQUEST-{request.Number:D03}.txt",
                    request.Content);

                //TODO: HandleResponse(request, assistantMessage);
            }
            saveAction();
        }
    }
}