using FunscriptToolbox.SubtitlesVerbs.Translations;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class Transcription
    {
        public string Id { get; private set; }
        public Language Language { get; }
        public bool IsFinished { get; private set; }
        public List<TranscribedText> Items { get; }
        public List<TranscriptionCost> Costs { get; }
        public List<Translation> Translations { get; }

        public Transcription(
            string id,
            Language language,
            bool? isFinished = false,
            IEnumerable<TranscribedText> items = null, 
            IEnumerable<TranscriptionCost> costs = null,
            IEnumerable<Translation> translations = null)
        {
            Id = id;
            Language = language ?? TranslatorGoogleV1API.DetectLanguage(items);
            IsFinished = isFinished ?? true;
            Items = new List<TranscribedText>(items ?? Array.Empty<TranscribedText>());
            Costs = new List<TranscriptionCost>(costs ?? Array.Empty<TranscriptionCost>());
            Translations = new List<Translation>(translations ?? Array.Empty<Translation>());
        }

        public void Rename(string newId)
        {
            this.Id = newId;
        }

        public void MarkAsFinished()
        {
            this.IsFinished = true;
        }

        public TranscriptionAnalysis<SubtitleForcedTiming> GetAnalysis(
            SubtitleGeneratorContext context)
        {
            return GetAnalysis(
                context.CurrentWipsub.SubtitlesForcedTiming?.Where(f => f.VoiceText != null).ToArray());
        }

        public TranscriptionAnalysis<T> GetAnalysis<T>(
            T[] timings) where T: class, ITiming
        {
            return (timings == null)
                    ? null :
                    TranscriptionAnalysis<T>.From(
                        this,
                        timings);
        }

        public int RemoveTranslation(string translationId)
        {
            var nbTranslatedTexts = 0;
            foreach (var translation in this
                .Translations
                .Where(t => t.Id == translationId)
                .ToArray())
            {
                this.Translations.Remove(translation);
            }

            foreach (var transcribedText in this.Items)
            {
                foreach (var translatedText in transcribedText
                    .TranslatedTexts
                    .Where(tt => tt.Id == translationId)
                    .ToArray())
                {
                    transcribedText.TranslatedTexts.Remove(translatedText);
                    nbTranslatedTexts++;
                }
            }
            return nbTranslatedTexts;
        }
    }
}