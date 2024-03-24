using FunscriptToolbox.SubtitlesVerbsV2.Translations;
using FunscriptToolbox.SubtitlesVerbV2;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public class WhisperTranscriberSingleVADAudio : WhisperTranscriber
    {
        public WhisperTranscriberSingleVADAudio(
            string transcriptionId,
            IEnumerable<Translator> translators,
            PurfviewWhisperConfig whisperConfig)
            : base(transcriptionId, translators, whisperConfig)
        {
        }

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

            var audioSections = subtitlesForcedLocation
                .Where(f => f.Type == SubtitleLocationType.Voice)
                .Select(
                    vad => pcmAudio.ExtractSnippet(vad.StartTime, vad.EndTime))
                .ToArray();
            var transcribedTexts = this.WhisperHelper.TranscribeAudio(
                audioHelper,
                audioSections,
                transcribedLanguage,
                out var costs);
            return new Transcription(
                this.TranscriptionId,
                transcribedLanguage,
                transcribedTexts, 
                costs);
        }
    }
}