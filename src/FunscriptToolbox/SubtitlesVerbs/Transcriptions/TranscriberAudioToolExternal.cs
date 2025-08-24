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
    public class TranscriberAudioToolExternal : TranscriberAudioTool
    {
        private const string ToolName = "AudioExternal";

        public TranscriberAudioToolExternal()
        {
        }

        [JsonProperty(Order = 21)]
        public string OverrideFileSuffixe { get; set; } = null;

        public override void TranscribeAudio(
            SubtitleGeneratorContext context,
            Transcription transcription,
            TimedObjectWithMetadata<PcmAudio>[] items)
        {
            var namedItems = items.Where(item => item.Tag != null).Select((item, index) => (
                    wavFilename: context.CurrentBaseFilePath + $".TODO-{transcription.Id}_{index + 1:D5}.wav",
                    srtFilename: context.CurrentBaseFilePath + $".TODO-{transcription.Id}_{index + 1:D5}.srt",
                    item
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
                foreach (var (wavFilename, srtFilename, item) in namedItems)
                {
                    if (!File.Exists(wavFilename))
                    {
                        context.FfmpegAudioHelper.ConvertPcmAudioToWavFile(item.Tag, wavFilename);
                    }
                    userTodos.Add($"Use external tool to transcribe '{Path.GetFileName(wavFilename)}'.");
                }

                throw new TranscriberNotReadyException("Transcribed .srt not provided yet.", userTodos);
            }
            else
            {
                var costsList = new List<TranscriptionCost>();
                var transcribedTexts = new List<TranscribedText>();
                foreach (var (wavFilename, srtFilename, item) in namedItems)
                {
                    var watch = Stopwatch.StartNew();
                    var subtitlesFile = SubtitleFile.FromSrtFile(srtFilename);

                    transcribedTexts.AddRange(
                        subtitlesFile
                        .Subtitles
                        .Select(subtitle => new TranscribedText(
                            item.Tag.Offset + subtitle.StartTime,
                            item.Tag.Offset + subtitle.EndTime,
                            subtitle.Text)));
                    transcription.Costs.Add(
                        new TranscriptionCost(
                            ToolName,
                            watch.Elapsed,
                            1,
                            item.Tag.Duration));

                    context.SoftDelete(wavFilename);
                    if (this.OverrideFileSuffixe == null)
                        context.SoftDelete(srtFilename);
                }
            }
        }
    }
}