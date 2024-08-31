using AudioSynchronization;
using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberMergedVADAudio : Transcriber
    {
        public TranscriberMergedVADAudio()
        {
        }

        [JsonProperty(Order = 20)]
        public TimeSpan SilentGapDuration { get; set; } = TimeSpan.FromSeconds(0.3);

        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            reason = "SubtitlesForcedTiming not imported yet.";
            return context.CurrentWipsub.SubtitlesForcedTiming != null;
        }

        public override Transcription Transcribe(
            SubtitleGeneratorContext context,
            PcmAudio pcmAudio,
            Language overrideLanguage)
        {
            var transcribedLanguage = overrideLanguage ?? this.Language;

            var silenceGapSamples = pcmAudio.GetSilenceAudio(this.SilentGapDuration);
            var halfSilenceGapLength = TimeSpan.FromMilliseconds(silenceGapSamples.Duration.TotalMilliseconds / 2);

            var currentDuration = TimeSpan.Zero;
            var audioOffsets = new List<AudioOffset>();
            var mergedAudio = new MemoryStream();

            var forcedTimings = context.CurrentWipsub.SubtitlesForcedTiming;

            for (int i = 0; i < forcedTimings.Count; i++)
            {
                var gapLengthBefore = i == 0 ? TimeSpan.Zero : forcedTimings[i].StartTime - forcedTimings[i - 1].EndTime;

                var startDuration = currentDuration;
                if (gapLengthBefore > this.SilentGapDuration)
                {
                    mergedAudio.Write(silenceGapSamples.Data, 0, silenceGapSamples.Data.Length / 2);
                    currentDuration += halfSilenceGapLength;
                }
                else
                {
                    // Do nothing
                }

                var partAudio = pcmAudio.ExtractSnippet(
                    forcedTimings[i].StartTime,
                    forcedTimings[i].EndTime);
                mergedAudio.Write(partAudio.Data, 0, partAudio.Data.Length);
                currentDuration += partAudio.Duration;

                if (i + 1 < forcedTimings.Count)
                {
                    var gapLengthAfter = forcedTimings[i + 1].StartTime - forcedTimings[i].EndTime;
                    if (gapLengthAfter > this.SilentGapDuration)
                    {
                        mergedAudio.Write(silenceGapSamples.Data, 0, silenceGapSamples.Data.Length / 2);
                        currentDuration += halfSilenceGapLength;
                    }
                    else if (gapLengthAfter > TimeSpan.Zero)
                    {
                        var audioInGap = pcmAudio.ExtractSnippet(
                            forcedTimings[i].EndTime,
                            forcedTimings[i + 1].StartTime);
                        mergedAudio.Write(audioInGap.Data, 0, audioInGap.Data.Length);
                        currentDuration += audioInGap.Duration;
                    }
                }

                audioOffsets.Add(
                    new AudioOffset(
                        startDuration,
                        currentDuration,
                        forcedTimings[i].StartTime - startDuration));
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

            if (context.IsVerbose)
            {
                var adjustedSrt = new SubtitleFile();
                adjustedSrt.Subtitles.AddRange(transcribedTexts.Select(tt => new Subtitle(tt.StartTime, tt.EndTime, tt.Text)));
                adjustedSrt.SaveSrt(context.GetPotentialVerboseFilePath($"{this.TranscriptionId}-adjusted.srt", DateTime.Now));
            }

            return new Transcription(
                this.TranscriptionId,
                transcribedLanguage,
                transcribedTexts,
                costs);
        }
    }
}