using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbV2;
using System;
using System.Linq;
using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbsV2.Outputs
{
    internal class SubtitleOutputSimpleSrt : SubtitleOutput
    {
        public SubtitleOutputSimpleSrt()
        {
            
        }

        public override bool NeedSubtitleForcedTimings => false;

        [JsonProperty(Order = 10)]
        public string FileSuffixe { get; set; }
        [JsonProperty(Order = 11)]
        public string TranscriptionId { get; set; }
        [JsonProperty(Order = 12)]
        public string TranslationId { get; set; }

        public override void CreateOutput(
            SubtitleGeneratorContext context,
            WorkInProgressSubtitles wipsub)
        {
            if (this.FileSuffixe == null)
            {
                throw new ArgumentNullException($"{typeof(SubtitleOutputWIPSrt).Name}.FileSuffixe");
            }

            var transcription = wipsub.Transcriptions.FirstOrDefault(t => t.Id == this.TranscriptionId) 
                ?? wipsub.Transcriptions.FirstOrDefault();
            if (transcription == null)
            {
                context.WriteError("   No transcription found."); // TODO Better message
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

            var filename = context.BaseFilePath + this.FileSuffixe;
            context.SoftDelete(filename);
            subtitleFile.SaveSrt(filename);
        }
    }
}
