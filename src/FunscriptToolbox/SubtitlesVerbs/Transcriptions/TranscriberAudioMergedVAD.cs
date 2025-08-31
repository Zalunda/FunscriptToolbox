using AudioSynchronization;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberAudioMergedVAD : Transcriber
    {
        [JsonProperty(Order = 20, Required = Required.Always)]
        internal MetadataAggregator Metadatas { get; set; }
        [JsonProperty(Order = 21, Required = Required.Always)]
        public string MetadataProduced { get; set; }
        [JsonProperty(Order = 22)]
        public TimeSpan SilentGapDuration { get; set; } = TimeSpan.FromSeconds(0.3);

        [JsonProperty(Order = 30, Required = Required.Always)]
        public TranscriberToolAudio TranscriberTool { get; set; }

        protected override string GetMetadataProduced() => this.MetadataProduced;

        protected override bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason)
        {
            if (!this.Metadatas.Aggregate(context).IsPrerequisitesMetWithTimings(out reason) == false)
            {
                return false;
            }

            reason = null;
            return true;
        }

        protected override void Transcribe(
            SubtitleGeneratorContext context,
            Transcription transcription)
        {
            var timings = this.Metadatas
                .Aggregate(context)
                .CreateRequestGenerator(transcription)
                .GetTimings();

            var pcmAudio = context.CurrentWipsub.PcmAudio;

            var silenceGapSamples = pcmAudio.GetSilenceAudio(this.SilentGapDuration);
            var halfSilenceGapLength = TimeSpan.FromMilliseconds(silenceGapSamples.Duration.TotalMilliseconds / 2);

            var currentDuration = TimeSpan.Zero;
            var audioOffsets = new List<AudioOffset>();
            var mergedAudio = new MemoryStream();

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

            var remappedItems = new List<TranscribedItem>();
            foreach (var original in this.TranscriberTool.TranscribeAudio(
                                context,
                                transcription,
                                new[] { mergedPcm },
                                this.MetadataProduced))
            {
                var newStartTime = offsetCollection.TransformPosition(original.StartTime);
                var newEndTime = offsetCollection.TransformPosition(original.EndTime);
                if (newStartTime == null || newEndTime == null)
                {
                    throw new Exception("BUG");
                }
                remappedItems.Add(
                    new TranscribedItem(
                        newStartTime.Value,
                        newEndTime.Value,
                        original.Metadata,
                        noSpeechProbability: original.NoSpeechProbability,
                        words: original
                            .Words
                            .Select(word => new TranscribedWord(
                                offsetCollection.TransformPosition(word.StartTime).Value,
                                offsetCollection.TransformPosition(word.EndTime).Value, 
                                word.Text, word.Probability))));

            }

            transcription.Items.AddRange(remappedItems);
            transcription.MarkAsFinished();
            context.CurrentWipsub.Save();

            SaveDebugSrtIfVerbose(context, transcription);
        }
    }
}