using FunscriptToolbox.Core;
using Newtonsoft.Json;
using System;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbsV2.Outputs
{
    internal class SubtitleOutputSingleTranslationSrt : SubtitleOutput
    {
        public SubtitleOutputSingleTranslationSrt()
        {
            
        }

        public override bool NeedSubtitleForcedTimings => false;

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [JsonProperty(Order = 11, Required = Required.Always)]
        public string TranscriptionId { get; set; }
        [JsonProperty(Order = 12, Required = Required.Always)]
        public string TranslationId { get; set; }

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

            var translation = transcription.Translations.FirstOrDefault(f => f.Id == this.TranslationId)
                ?? transcription.Translations.FirstOrDefault();

            var subtitleFile = new SubtitleFile();
            foreach (var item in transcription.Items)
            {
                var text = item.TranslatedTexts.FirstOrDefault(tt => tt.Id == translation?.Id)?.Text
                    ?? item.Text;
                subtitleFile.Subtitles.Add(new Subtitle(item.StartTime, item.EndTime, text));
            }

            var filename = context.CurrentBaseFilePath + this.FileSuffix;
            context.SoftDelete(filename);
            subtitleFile.SaveSrt(filename);
        }
    }
}
