using FunscriptToolbox.Core;
using Newtonsoft.Json;
using System;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    internal class SubtitleOutputSingleTranslationSrt : SubtitleOutput
    {
        public SubtitleOutputSingleTranslationSrt()
        {
            
        }

        public override bool NeedSubtitleForcedTimings => false;

        public override string Description => $"{base.Description}: {this.FileSuffix}";

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [JsonProperty(Order = 11, Required = Required.Always)]
        public string TranscriptionId { get; set; }
        [JsonProperty(Order = 12)]
        public string TranslationId { get; set; }

        [JsonProperty(Order = 20)]
        public SubtitleToInject[] SubtitlesToInject { get; set; }

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            reason = $"Cannot create file because transcription '{this.TranscriptionId}' doesn't exists yet.";
            return (this.TranscriptionId == null) || (null != (context.CurrentWipsub.Transcriptions.FirstOrDefault(t => t.Id == this.TranscriptionId)
                ?? context.CurrentWipsub.Transcriptions.FirstOrDefault()));
        }

        public override void CreateOutput(
            SubtitleGeneratorContext context)
        {
            var transcription = context.CurrentWipsub.Transcriptions.FirstOrDefault(t => t.Id == this.TranscriptionId);
            var translation = transcription.Translations.FirstOrDefault(f => f.Id == this.TranslationId);

            var subtitleFile = new SubtitleFile();
            foreach (var item in transcription.Items)
            {
                var text = item.TranslatedTexts.FirstOrDefault(tt => tt.Id == translation?.Id)?.Text
                    ?? item.Text;
                subtitleFile.Subtitles.Add(new Subtitle(item.StartTime, item.EndTime, text));
            }

            subtitleFile.Subtitles.AddRange(
                GetAdjustedSubtitlesToInject(subtitleFile.Subtitles, this.SubtitlesToInject, context.CurrentWipsub.PcmAudio.Duration));

            var filename = context.CurrentBaseFilePath + this.FileSuffix;
            context.SoftDelete(filename);
            subtitleFile.SaveSrt(filename);
        }
    }
}
