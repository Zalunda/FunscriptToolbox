using FunscriptToolbox.SubtitlesVerbsV2.Translations;
using FunscriptToolbox.SubtitlesVerbV2;
using System.Collections.Generic;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public class WhisperTranscriberFullAudio : WhisperTranscriber
    {
        public WhisperTranscriberFullAudio(
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
            var transcribedTexts = this.WhisperHelper.TranscribeAudio(
                     audioHelper,
                     new[] { pcmAudio },
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