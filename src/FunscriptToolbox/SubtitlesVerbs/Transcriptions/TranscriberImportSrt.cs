using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberImportSrt : Transcriber
    {
        [JsonProperty(Order = 10, Required = Required.Always)]
        public string FileSuffix { get; set; } = null;
        
        public override bool IsPrerequisitesMet(SubtitleGeneratorContext context, out string reason)
        {
            var fullpath = context.CurrentBaseFilePath + this.FileSuffix;
            if (File.Exists(fullpath))
            {
                reason = null;
                return true;
            }
            else
            {
                reason = $"Can't find file '{Path.GetFileName(fullpath)}'";
                return false;
            }
        }

        public override Transcription Transcribe(SubtitleGeneratorContext context, PcmAudio pcmAudio, Language overrideLanguage)
        {
            var fullpath = context.CurrentBaseFilePath + this.FileSuffix;

            var transcribedLanguage = overrideLanguage ?? this.Language;
            var transcribedTexts = SubtitleFile
                .FromSrtFile(fullpath)
                .Subtitles
                .Select(f => new TranscribedText(f.StartTime, f.EndTime, f.Text));
            return new Transcription(
                this.TranscriptionId,
                transcribedLanguage,
                transcribedTexts,
                Array.Empty<TranscriptionCost>());
        }
    }
}