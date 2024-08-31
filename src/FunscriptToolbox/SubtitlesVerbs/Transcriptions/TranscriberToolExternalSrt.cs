using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
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

        public override TranscribedText[] TranscribeAudio(
            SubtitleGeneratorContext context,
            ProgressUpdateDelegate progressUpdateCallback,
            PcmAudio[] audios,
            Language sourceLanguage,
            string filesPrefix,
            out TranscriptionCost[] costs)
        {
            var namedItems = audios.Select((audio, index) => new
                {
                    Filename = context.CurrentBaseFilePath + $".TODO-{filesPrefix}{index + 1:D5}.wav",
                    Audio = audio
                });

            if (namedItems.Any(item => !File.Exists(Path.ChangeExtension(item.Filename, ".srt"))))
            {
                var userTodos = new List<string>();
                foreach (var item in namedItems)
                {
                    if (!File.Exists(item.Filename))
                    {
                        context.FfmpegAudioHelper.ConvertPcmAudioToWavFile(item.Audio, item.Filename);
                    }
                    userTodos.Add($"Use external tool to transcribe '{Path.GetFileName(item.Filename)}'.");
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
                    var srtFilename = Path.ChangeExtension(item.Filename, ".srt");
                    var subtitlesFile = SubtitleFile.FromSrtFile(srtFilename);

                    transcribedTexts.AddRange(
                        subtitlesFile
                        .Subtitles
                        .Select(subtitle => new TranscribedText(
                            item.Audio.Offset + subtitle.StartTime,
                            item.Audio.Offset + subtitle.EndTime,
                            subtitle.Text)));
                    costsList.Add(
                        new TranscriptionCost(
                            ToolName,
                            watch.Elapsed,
                            1,
                            item.Audio.Duration));

                    context.SoftDelete(item.Filename);
                    context.SoftDelete(srtFilename);
                }
                costs = costsList.ToArray();
                return transcribedTexts.ToArray();
            }
        }
    }
}