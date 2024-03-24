using AudioSynchronization;
using FunscriptToolbox.SubtitlesVerbV2;
using System.Collections.Generic;
using System.IO;
using System;
using FunscriptToolbox.SubtitlesVerbsV2.Translations;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public class WhisperTranscriberMergedVADAudio : WhisperTranscriber
    {
        public WhisperTranscriberMergedVADAudio(
            string transcriptionId,
            IEnumerable<Translator> translators,
            PurfviewWhisperConfig whisperConfig)
            : base(transcriptionId, translators, whisperConfig)
        {
        }

        public TimeSpan GapLength { get; set; } = TimeSpan.FromSeconds(0.3);

        public override Transcription Transcribe(
            FfmpegAudioHelper audioHelper,
            PcmAudio pcmAudio,
            IEnumerable<SubtitleForcedLocation> subtitlesForcedLocation,
            Language overrideLanguage)
        {
            var transcribedLanguage = overrideLanguage ?? this.Language;
            if (subtitlesForcedLocation == null)
            {
                // TODO Maybe add a PrerequisiteMet method
                return null;
            }

            var silenceGapSamples = pcmAudio.GetSilenceAudio(this.GapLength);

            var currentDuration = TimeSpan.Zero;
            var audioOffsets = new List<AudioOffset>();
            var mergedAudio = new MemoryStream();
            foreach (var forcedLocation in subtitlesForcedLocation)
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
            foreach (var original in this.WhisperHelper.TranscribeAudio(
                                audioHelper,
                                new[] { mergedPcm },
                                transcribedLanguage,
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