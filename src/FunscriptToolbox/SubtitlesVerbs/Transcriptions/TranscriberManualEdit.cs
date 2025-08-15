using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberManualEdit : Transcriber
    {
        public TranscriberManualEdit()
        {
        }

        [JsonProperty(Order = 21, Required = Required.Always)]
        public string SourceTranscriptionId { get; set; } = null;

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            IEnumerable<Transcriber> transcribers,
            out string reason)
        {
            if (!context.CurrentWipsub.Transcriptions.Any(f => f.Id == this.SourceTranscriptionId))
            {
                reason = $"Transcription '{this.SourceTranscriptionId}' not done yet.";
                return false;
            }
            else
            {
                reason = null;
                return true;
            }
        }

        public override Transcription Transcribe(
            SubtitleGeneratorContext context,
            PcmAudio pcmAudio,
            Language overrideLanguage)
        {
            var transcribedLanguage = overrideLanguage ?? this.Language;
            var sourceTranscription = context.CurrentWipsub.Transcriptions.First(f => f.Id == this.SourceTranscriptionId);
            var sourceSrt = context.CurrentBaseFilePath + $".TODO-not-fixed.srt";
            var fixedSrt = context.CurrentBaseFilePath + $".TODO-fixed.srt";

            if (!File.Exists(fixedSrt))
            {
                var subtitleFile = new SubtitleFile(
                    sourceSrt,
                    sourceTranscription.Items.Select(item => new Subtitle(item.StartTime, item.EndTime, item.Text)));
                if (context.CurrentWipsub.SubtitlesForcedTiming != null)
                {
                    var transcriptionsAnalysis = sourceTranscription.GetAnalysis(context);
                    subtitleFile.Subtitles.AddRange(
                        transcriptionsAnalysis.TimingsWithoutTranscription
                        .Select(t => new Subtitle(t.StartTime, t.EndTime, "** MISSING TRANSCRIPTION **")));
                }
                context.SoftDelete(sourceSrt);
                subtitleFile.SaveSrt();
                context.UserTodoList.Add($"Manually fix transcriptions in '{Path.GetFileName(sourceSrt)}' then rename the file to '{Path.GetFileName(fixedSrt)}'.");
                throw new TranscriberNotReadyException("Manually fixed .srt not provided yet.");
            }
            else
            {
                var transcribedTexts = new List<TranscribedText>();
                var fixedSubtitleFile = SubtitleFile.FromSrtFile(fixedSrt);
                var allWords = sourceTranscription.Items.SelectMany(item => item.Words).ToArray();
                foreach (var subtitle in fixedSubtitleFile.Subtitles)
                {
                    transcribedTexts.Add(
                        new TranscribedText(
                            subtitle.StartTime,
                            subtitle.EndTime,
                            subtitle.Text,
                    words: allWords
                                .Where(word => word.StartTime < subtitle.EndTime && word.EndTime > subtitle.StartTime)));
                }
                return new Transcription(
                    this.TranscriptionId,
                    transcribedLanguage,
                    transcribedTexts,
                    Array.Empty<TranscriptionCost>());
            }
        }
    }
}