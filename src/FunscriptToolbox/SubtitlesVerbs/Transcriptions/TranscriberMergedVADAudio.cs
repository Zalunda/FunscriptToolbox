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

        [JsonProperty(Order = 20, Required = Required.Always)]
        public TranscriberTool TranscriberTool { get; set; }

        [JsonProperty(Order = 21)]
        public TimeSpan SilentGapDuration { get; set; } = TimeSpan.FromSeconds(0.3);

        [JsonProperty(Order = 22)]
        public string UseTimingsFromId { get; set; } = null;
        
        public override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            IEnumerable<Transcriber> transcribers,
            out string reason)
        {
            if (this.UseTimingsFromId != null && !context.CurrentWipsub.Transcriptions.Any(f => f.Id == this.UseTimingsFromId))
            {
                reason = $"Transcription '{this.UseTimingsFromId}' not done yet.";
                return false;
            }
            else
            {
                reason = "SubtitlesForcedTiming not imported yet.";
                return context.CurrentWipsub.SubtitlesForcedTiming != null;
            }
        }

        public override void Transcribe(
            SubtitleGeneratorContext context,
            Transcription transcription,
            PcmAudio pcmAudio,
            Language overrideLanguage)
        {
            var transcribedLanguage = overrideLanguage ?? this.Language;

            var silenceGapSamples = pcmAudio.GetSilenceAudio(this.SilentGapDuration);
            var halfSilenceGapLength = TimeSpan.FromMilliseconds(silenceGapSamples.Duration.TotalMilliseconds / 2);

            var currentDuration = TimeSpan.Zero;
            var audioOffsets = new List<AudioOffset>();
            var mergedAudio = new MemoryStream();

            var timings = UseTimingsFromId == null 
                ? context.CurrentWipsub.SubtitlesForcedTiming.Where(f => f.VoiceText != null).Cast<ITiming>().ToArray()
                : context.CurrentWipsub.Transcriptions.FirstOrDefault(f => f.Id == this.UseTimingsFromId).Items.Cast<ITiming>().ToArray();

            for (int i = 0; i < timings.Length; i++)
            {
                var gapLengthBefore = i == 0 ? TimeSpan.Zero : timings[i].StartTime - timings[i - 1].EndTime;

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
                    timings[i].StartTime,
                    timings[i].EndTime);
                mergedAudio.Write(partAudio.Data, 0, partAudio.Data.Length);
                currentDuration += partAudio.Duration;

                if (i + 1 < timings.Length)
                {
                    var gapLengthAfter = timings[i + 1].StartTime - timings[i].EndTime;
                    if (gapLengthAfter > this.SilentGapDuration)
                    {
                        mergedAudio.Write(silenceGapSamples.Data, 0, silenceGapSamples.Data.Length / 2);
                        currentDuration += halfSilenceGapLength;
                    }
                    else if (gapLengthAfter > TimeSpan.Zero)
                    {
                        var audioInGap = pcmAudio.ExtractSnippet(
                            timings[i].EndTime,
                            timings[i + 1].StartTime);
                        mergedAudio.Write(audioInGap.Data, 0, audioInGap.Data.Length);
                        currentDuration += audioInGap.Duration;
                    }
                }

                audioOffsets.Add(
                    new AudioOffset(
                        startDuration,
                        currentDuration,
                        timings[i].StartTime - startDuration));
            }

            var offsetCollection = new AudioOffsetCollection(audioOffsets);
            var mergedPcm = new PcmAudio(pcmAudio.SamplingRate, mergedAudio.ToArray());

            this.TranscriberTool.TranscribeAudio(
                                context,
                                context.DefaultProgressUpdateHandler,
                                transcription,
                                new[] { mergedPcm },
                                $"{this.TranscriptionId}-");
            var oldItems = transcription.Items.ToArray();
            transcription.Items.Clear();
            var remappedItems = new List<TranscribedText>();

            foreach (var original in oldItems)
            {
                var newStartTime = offsetCollection.TransformPosition(original.StartTime);
                var newEndTime = offsetCollection.TransformPosition(original.EndTime);
                if (newStartTime == null || newEndTime == null)
                {
                    throw new Exception("BUG");
                }
                remappedItems.Add(
                    new TranscribedText(
                        newStartTime.Value,
                        newEndTime.Value,
                        original.Text,
                        original.NoSpeechProbability,
                        original
                            .Words
                            .Select(word => new TranscribedWord(
                                offsetCollection.TransformPosition(word.StartTime).Value,
                                offsetCollection.TransformPosition(word.EndTime).Value, 
                                word.Text, word.Probability))));

            }

            if (context.IsVerbose)
            {
                var adjustedSrt = new SubtitleFile();
                adjustedSrt.Subtitles.AddRange(transcription.Items.Select(tt => new Subtitle(tt.StartTime, tt.EndTime, tt.Text)));
                adjustedSrt.SaveSrt(context.GetPotentialVerboseFilePath($"{this.TranscriptionId}-adjusted.srt", DateTime.Now));
            }

            transcription.Items.AddRange(remappedItems);
            transcription.MarkAsFinished();
        }
    }
}