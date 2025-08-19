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
                    WavFilename: context.CurrentBaseFilePath + $".TODO-{filesPrefix}{index + 1:D5}.wav",
                    SrtFilename: context.CurrentBaseFilePath + $".TODO-{filesPrefix}{index + 1:D5}.srt",
                    Audio: audio
                )).ToArray();
            if (this.OverrideFileSuffixe != null && namedItems.Length > 0)
            {
                namedItems[0].SrtFilename = (namedItems.Length <= 1)
                    ? context.CurrentBaseFilePath + this.OverrideFileSuffixe 
                    : throw new Exception($"Can't use OverrideFileSuffixe when the number of audio file is more then 1.");
            }

            if (namedItems.Any(item => !File.Exists(item.SrtFilename)))
            {
                var userTodos = new List<string>();
                foreach (var item in namedItems)
                {
                    if (!File.Exists(item.WavFilename))
                    {
                        context.FfmpegAudioHelper.ConvertPcmAudioToWavFile(item.Audio, item.WavFilename);
                    }
                    userTodos.Add($"Use external tool to transcribe '{Path.GetFileName(item.WavFilename)}'.");
                }

                throw new TranscriberNotReadyException("Transcribed .srt not provided yet.", userTodos);
            }
            else
            {
                var costsList = new List<TranscriptionCost>();
                var transcribedTexts = new List<TranscribedText>();
                foreach (var item in namedItems)
                {
                    var watch = Stopwatch.StartNew();
                    var srtFilename = item.SrtFilename;
                    var subtitlesFile = SubtitleFile.FromSrtFile(srtFilename);

                    transcribedTexts.AddRange(
                        subtitlesFile
                        .Subtitles
                        .Select(subtitle => new TranscribedText(
                            item.Audio.Offset + subtitle.StartTime,
                            item.Audio.Offset + subtitle.EndTime,
                            subtitle.Text)));
                    transcription.Costs.Add(
                        new TranscriptionCost(
                            ToolName,
                            watch.Elapsed,
                            1,
                            item.Audio.Duration));

                    context.SoftDelete(item.WavFilename);
                    if (this.OverrideFileSuffixe == null)
                        context.SoftDelete(item.SrtFilename);
                }
            }
        }
    }
}