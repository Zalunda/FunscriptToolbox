using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using FunscriptToolbox.SubtitlesVerbs.Infra;
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
        [JsonProperty(Order = 22)]
        public string MetadataProduced { get; set; } = "VoiceText";

        public override TranscribedItem[] TranscribeAudio(
            SubtitleGeneratorContext context,
            Transcription transcription,
            PcmAudio[] audios)
        {
            var namedItems = audios.Select((audio, index) => (
                    wavFilename: context.CurrentBaseFilePath + $".TODO-{transcription.Id}_{index + 1:D5}.wav",
                    srtFilename: context.CurrentBaseFilePath + $".TODO-{transcription.Id}_{index + 1:D5}.srt",
                    audio
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
                foreach (var (wavFilename, srtFilename, audio) in namedItems)
                {
                    if (!File.Exists(wavFilename))
                    {
                        context.FfmpegAudioHelper.ConvertPcmAudioToWavFile(audio, wavFilename);
                    }
                    userTodos.Add($"Use external tool to transcribe '{Path.GetFileName(wavFilename)}'.");
                }

                throw new TranscriberNotReadyException("Transcribed .srt not provided yet.", userTodos);
            }
            else
            {
                var costsList = new List<Cost>();
                var transcribedItems = new List<TranscribedItem>();
                foreach (var (wavFilename, srtFilename, audio) in namedItems)
                {
                    var watch = Stopwatch.StartNew();
                    var subtitlesFile = SubtitleFile.FromSrtFile(srtFilename);
                    transcribedItems.AddRange(subtitlesFile
                        .Subtitles
                        .Select(subtitle => new TranscribedItem(
                            audio.Offset + subtitle.StartTime,
                            audio.Offset + subtitle.EndTime,
                            MetadataCollection.CreateSimple(this.MetadataProduced, subtitle.Text))));
                    transcription.Costs.Add(
                        new Cost(
                            ToolName,
                            watch.Elapsed,
                            1,
                            itemsDuration: audio.Duration));

                    context.SoftDelete(wavFilename);
                    if (this.OverrideFileSuffixe == null)
                    {
                        context.SoftDelete(srtFilename);
                    }
                }
                return transcribedItems.ToArray();
            }
        }
    }
}