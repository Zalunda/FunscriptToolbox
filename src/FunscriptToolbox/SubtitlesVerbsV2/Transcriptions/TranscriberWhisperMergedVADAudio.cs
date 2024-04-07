using AudioSynchronization;
using FunscriptToolbox.SubtitlesVerbV2;
using System.Collections.Generic;
using System.IO;
using System;
using Newtonsoft.Json;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public class TranscriberWhisperMergedVADAudio : TranscriberWhisper
    {
        public TranscriberWhisperMergedVADAudio()
        {
        }

        [JsonProperty(Order = 20)]
        public TimeSpan GapLength { get; set; } = TimeSpan.FromSeconds(0.3);

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            reason = "SubtitlesForcedTiming not imported yet";
            return context.Wipsub.SubtitlesForcedTiming != null;
        }

        public override Transcription Transcribe(
            SubtitleGeneratorContext context,
            PcmAudio pcmAudio,
            Language overrideLanguage)
        {
            var transcribedLanguage = overrideLanguage ?? this.Language;

            var silenceGapSamples = pcmAudio.GetSilenceAudio(this.GapLength);

            var currentDuration = TimeSpan.Zero;
            var audioOffsets = new List<AudioOffset>();
            var mergedAudio = new MemoryStream();
            foreach (var forcedLocation in context.Wipsub.SubtitlesForcedTiming)
            {
                var partAudio = pcmAudio.ExtractSnippet(
                    forcedLocation.StartTime, 
                    forcedLocation.EndTime);

                audioOffsets.Add(
                    new AudioOffset(
                        currentDuration,
                        currentDuration + partAudio.Duration + silenceGapSamples.Duration,
                        forcedLocation.StartTime - currentDuration));

                mergedAudio.Write(silenceGapSamples.Data, 0, silenceGapSamples.Data.Length / 2);
                mergedAudio.Write(partAudio.Data, 0, partAudio.Data.Length);
                mergedAudio.Write(silenceGapSamples.Data, 0, silenceGapSamples.Data.Length / 2);
                currentDuration += partAudio.Duration;
                currentDuration += silenceGapSamples.Duration;
            }

            var offsetCollection = new AudioOffsetCollection(audioOffsets);
            var mergedPcm = new PcmAudio(pcmAudio.SamplingRate, mergedAudio.ToArray());
            var transcribedTexts = new List<TranscribedText>();
            TranscriptionCost[] costs;
            foreach (var original in this.TranscriberTool.TranscribeAudio(
                                context,
                                context.DefaultProgressUpdateHandler,
                                new[] { mergedPcm },
                                transcribedLanguage,
                                $"{this.TranscriptionId}-",
                                out costs))
            {
                var newStartTime = offsetCollection.TransformPosition(original.StartTime);
                var newEndTime = offsetCollection.TransformPosition(original.EndTime);
                if (newStartTime == null || newEndTime == null)
                {
                    throw new Exception("BUG");
                }
                transcribedTexts.Add(
                    new TranscribedText(
                        newStartTime.Value,
                        newEndTime.Value,
                        original.Text,
                        original.NoSpeechProbability,
                        original.Words));

            }

            return new Transcription(
                this.TranscriptionId,
                transcribedLanguage,
                transcribedTexts,
                costs);
        }
    }
}