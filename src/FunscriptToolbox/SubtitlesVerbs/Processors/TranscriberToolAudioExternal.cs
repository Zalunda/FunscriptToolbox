using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.Inputs;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Processors
{
    [ConfigDoc("A transcription done by you, with another tool: the audio is written next to the video ('.TODO-<id>_<n>.wav'), and the scene waits for the .srt you make from it (same name, '.srt'; OverrideFileSuffixe: another name, one audio only), then imports it.")]
    public class TranscriberToolAudioExternal : TranscriberToolAudio
    {
        private const string ToolName = "AudioExternal";

        public TranscriberToolAudioExternal()
        {
        }

        [ConfigDoc("The suffix of the .srt to import instead of '.TODO-<id>_<n>.srt' (ex. \".whisper.srt\"). Only when there is one audio (an error otherwise).")]
        [JsonProperty(Order = 21)]
        public string OverrideFileSuffixe { get; set; } = null;

        public override TimedItemWithMetadata[] TranscribeAudio(
            SubtitleGeneratorContext context,
            Track transcription,
            PcmAudio[] audios,
            string metadataProduced)
        {
            var namedItems = audios.Select((audio, index) => (
                    wavFilename: context.WIP.BaseFilePath + $".TODO-{transcription.Id}_{index + 1:D5}.wav",
                    srtFilename: context.WIP.BaseFilePath + $".TODO-{transcription.Id}_{index + 1:D5}.srt",
                    audio
                )).ToArray();
            if (this.OverrideFileSuffixe != null && namedItems.Length > 0)
            {
                namedItems[0].srtFilename = (namedItems.Length <= 1)
                    ? context.WIP.BaseFilePath + this.OverrideFileSuffixe 
                    : throw new Exception($"Can't use OverrideFileSuffixe when the number of audio file is more then 1.");
            }

            if (namedItems.Any(item => !File.Exists(item.srtFilename)))
            {
                var userTodos = new List<string>();
                foreach (var (wavFilename, srtFilename, audio) in namedItems)
                {
                    if (!File.Exists(wavFilename))
                    {
                        context.FfmpegHelper.ConvertPcmAudioToOtherFormat(audio, wavFilename);
                    }
                    userTodos.Add($"Use external tool to transcribe '{Path.GetFileName(wavFilename)}'.");
                }

                throw new TranscriberNotReadyException("Transcribed .srt not provided yet.", userTodos);
            }
            else
            {
                var costsList = new List<Cost>();
                var transcribedItems = new List<TimedItemWithMetadata>();
                foreach (var (wavFilename, srtFilename, audio) in namedItems)
                {
                    var watch = Stopwatch.StartNew();
                    var subtitlesFile = SubtitleFile.FromSrtFile(srtFilename);
                    transcribedItems.AddRange(subtitlesFile
                        .Subtitles
                        .Select(subtitle => new TimedItemWithMetadata(
                            audio.Offset + subtitle.StartTime,
                            audio.Offset + subtitle.EndTime,
                            MetadataCollection.CreateSimple(metadataProduced, subtitle.Text))));
                    transcription.Costs.Add(
                        new Cost(transcription.Id, ToolName, watch.Elapsed, 1));

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