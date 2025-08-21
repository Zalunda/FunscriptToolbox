using FunscriptToolbox.Core;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    internal class SubtitleOutputMultiTranslationSrt : SubtitleOutput
    {
        public SubtitleOutputMultiTranslationSrt()
        {
            
        }

        public override bool NeedSubtitleForcedTimings => false;

        public override string Description => $"{base.Description}: {this.FileSuffix}" ;

        [JsonProperty(Order = 10, Required = Required.Always)]
        public string FileSuffix { get; set; }
        [JsonProperty(Order = 11, Required = Required.Always)]
        public string TranscriptionId { get; set; }
        [JsonProperty(Order = 12, Required = Required.Always)]
        public string[] TranslationsOrder { get; set; }
        [JsonProperty(Order = 13)]
        public TimeSpan MinimumSubtitleDuration { get; set; } = TimeSpan.FromSeconds(1.5);
        [JsonProperty(Order = 14)]
        public TimeSpan ExpandSubtileDuration { get; set; } = TimeSpan.FromSeconds(0.5);

        [JsonProperty(Order = 20)]
        public SubtitleToInjectCollection SubtitlesToInject { get; set; }

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            reason = $"Cannot create file because transcription '{this.TranscriptionId}' doesn't exists yet.";
            return null != (context.CurrentWipsub.Transcriptions.FirstOrDefault(t => t.Id == this.TranscriptionId)
                ?? context.CurrentWipsub.Transcriptions.FirstOrDefault());
        }

        public override void CreateOutput(
            SubtitleGeneratorContext context)
        {
            var transcription = context.CurrentWipsub.Transcriptions.FirstOrDefault(t => t.Id == this.TranscriptionId) 
                ?? context.CurrentWipsub.Transcriptions.FirstOrDefault();
            if (transcription == null)
            {
                context.WriteError($"Cannot create file '{this.FileSuffix}' because transcription '{this.TranscriptionId}' doesn't exists.");
                return;
            }

            var finalTranslationOrder = CreateFinalOrder(
                this.TranslationsOrder, 
                context.CurrentWipsub.Transcriptions.SelectMany(f => f.Translations.Select(f2 => f2.Id)));

            var subtitleFile = new SubtitleFile();
            foreach (var transcribedText in transcription.Items)
            {
                var builder = new StringBuilder();
                builder.AppendLine(transcribedText.Text);
                foreach (var translatedText in transcribedText
                    .TranslatedTexts
                    .Where(f => finalTranslationOrder.Contains(f.Id))
                    .OrderBy(f => Array.IndexOf(finalTranslationOrder, f.Id)))
                {
                    builder.AppendLine($"   [{translatedText.Id}] {translatedText.Text}");
                }
                subtitleFile.Subtitles.Add(new Subtitle(transcribedText.StartTime, transcribedText.EndTime, builder.ToString()));
            }

            // Apply minimum duration and expansion
            subtitleFile.ExpandTiming(this.MinimumSubtitleDuration, this.ExpandSubtileDuration);

            subtitleFile.Subtitles.AddRange(
                GetAdjustedSubtitlesToInject(
                    subtitleFile.Subtitles,
                    this.SubtitlesToInject,
                    context.CurrentWipsub.PcmAudio.Duration));

            var filename = context.CurrentBaseFilePath + this.FileSuffix;
            context.SoftDelete(filename);
            subtitleFile.SaveSrt(filename);
        }
    }
}
