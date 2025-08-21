using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberToolExternalSrt : TranscriberTool
    {
        private const string ToolName = "ExternalSrt";

        public TranscriberToolExternalSrt()
        {
        }

        [JsonProperty(Order = 21)]
        public string OverrideFileSuffixe { get; set; } = null;

        public override void TranscribeAudio(
            SubtitleGeneratorContext context,
            ProgressUpdateDelegate progressUpdateCallback,
            Transcription transcription,
            PcmAudio[] audios,
            string filesPrefix)
        {
            var namedItems = audios.Select((audio, index) => (
                    wavFilename: context.CurrentBaseFilePath + $".TODO-{filesPrefix}{index + 1:D5}.wav",
                    srtFilename: context.CurrentBaseFilePath + $".TODO-{filesPrefix}{index + 1:D5}.srt",
                    audio: audio
                )).ToArray();
            if (this.OverrideFileSuffixe != null && namedItems.Length > 0)
            {
                namedItems[0].srtFilename = (namedItems.Length <= 1)
                    ? context.CurrentBaseFilePath + this.OverrideFileSuffixe 
                    : throw new Exception($"Can't use OverrideFileSuffixe when the number of audio file is more then 1.");
            }

            if (namedItems.Any(item => !File.Exists(item.srtFilename)))
            {
                var userTodos = new List<string>();
                foreach (var item in namedItems)
                {
                    if (!File.Exists(item.wavFilename))
                    {
                        context.FfmpegAudioHelper.ConvertPcmAudioToWavFile(item.audio, item.wavFilename);
                    }
                    userTodos.Add($"Use external tool to transcribe '{Path.GetFileName(item.wavFilename)}'.");
                }

                throw new TranscriberNotReadyException("Transcribed .srt not provided yet.", userTodos);
            }
            else
            {
                var costsList = new List<TranscriptionCost>();
                var transcribedTexts = new List<TranscribedText>();
                foreach (var (wavFilename, srtFilename, audio) in namedItems)
                {
                    var watch = Stopwatch.StartNew();
                    var subtitlesFile = SubtitleFile.FromSrtFile(srtFilename);

                    transcribedTexts.AddRange(
                        subtitlesFile
                        .Subtitles
                        .Select(subtitle => new TranscribedText(
                            audio.Offset + subtitle.StartTime,
                            audio.Offset + subtitle.EndTime,
                            subtitle.Text)));
                    transcription.Costs.Add(
                        new TranscriptionCost(
                            ToolName,
                            watch.Elapsed,
                            1,
                            audio.Duration));

                    context.SoftDelete(wavFilename);
                    if (this.OverrideFileSuffixe == null)
                        context.SoftDelete(srtFilename);
                }
            }
        }
    }
}