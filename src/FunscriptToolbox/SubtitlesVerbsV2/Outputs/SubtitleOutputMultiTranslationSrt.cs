using FunscriptToolbox.Core;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbsV2.Outputs
{
    internal class SubtitleOutputMultiTranslationSrt : SubtitleOutput
    {
        public SubtitleOutputMultiTranslationSrt()
        {
            
        }

        public override bool NeedSubtitleForcedTimings => false;

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [JsonProperty(Order = 11, Required = Required.Always)]
        public string TranscriptionId { get; set; }
        [JsonProperty(Order = 12, Required = Required.Always)]
        public string[] TranslationsOrder { get; set; }

        public override void CreateOutput(
            SubtitleGeneratorContext context,
            WorkInProgressSubtitles wipsub)
        {
            var transcription = wipsub.Transcriptions.FirstOrDefault(t => t.Id == this.TranscriptionId) 
                ?? wipsub.Transcriptions.FirstOrDefault();
            if (transcription == null)
            {
                context.WriteError($"Cannot create file '{this.FileSuffix}' because transcription '{this.TranscriptionId}' doesn't exists.");
                return;
            }

            var finalTranslationOrder = CreateFinalOrder(this.TranslationsOrder, wipsub.Transcriptions.SelectMany(f => f.Translations.Select(f2 => f2.Id)));

            var subtitleFile = new SubtitleFile();
            foreach (var transcribedText in transcription.Items)
            {
                var builder = new StringBuilder();
                foreach (var translatedText in transcribedText
                    .TranslatedTexts
                    .Where(f => finalTranslationOrder.Contains(f.Id))
                    .OrderBy(f => Array.IndexOf(finalTranslationOrder, f.Id)))
                {
                    builder.AppendLine($"   [{translatedText.Id}] {translatedText.Text}");
                }
                subtitleFile.Subtitles.Add(new Subtitle(transcribedText.StartTime, transcribedText.EndTime, builder.ToString()));
            }

            var filename = context.CurrentBaseFilePath + this.FileSuffix;
            context.SoftDelete(filename);
            subtitleFile.SaveSrt(filename);
        }
    }
}
