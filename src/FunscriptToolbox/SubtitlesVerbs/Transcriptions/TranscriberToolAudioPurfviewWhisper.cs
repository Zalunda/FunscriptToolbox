using FunscriptToolbox.Core;
using FunscriptToolbox.SubtitlesVerbs.AudioExtractions;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public class TranscriberToolAudioPurfviewWhisper : TranscriberToolAudio
    {
        private object r_lock = new object();

        private const string ToolName = "PurfviewWhisper";

        public TranscriberToolAudioPurfviewWhisper()
        {
        }

        [JsonProperty(Order = 1, Required = Required.Always)]
        public string ApplicationFullPath { get; set; }
        [JsonProperty(Order = 2)]
        public string AdditionalParameters { get; set; } = "";
        [JsonProperty(Order = 3, Required = Required.Always)]
        public string Model { get; set; } = "Large-V2";
        [JsonProperty(Order = 4)]
        public bool ForceSplitOnComma { get; set; } = true;
        [JsonProperty(Order = 5)]
        public TimeSpan IgnoreSubtitleShorterThen { get; set; } = TimeSpan.FromMilliseconds(20);

        public override TranscribedItem[] TranscribeAudio(
            SubtitleGeneratorContext context,
            Transcription transcription,
            PcmAudio[] items,
            string metadataProduced)
        {
            return TranscribeAudioInternal(
                context,
                transcription,
                items,
                metadataProduced);
        }

        private TranscribedItem[] TranscribeAudioInternal(
            SubtitleGeneratorContext context,
            Transcription transcription,
            PcmAudio[] audios,
            string metadataProduced)
        {
            if (!File.Exists(this.ApplicationFullPath))
            {
                throw new Exception($"Cannot find application '{this.ApplicationFullPath}'.");
            }

            // Only allows one thread doing transcription at a times
            lock (r_lock)
            {
                var tempFiles = new List<string>();
                var processStartTime = DateTime.Now;
                var fullSrtTempFile = context.GetPotentialVerboseFilePath($"{transcription.Id}_all.srt", processStartTime);
                var transcribedItems = new List<TranscribedItem>();

                try
                {
                    // Convert each input PCM audio to WAV format and store as temporary files
                    var indexAudio = 0;
                    var totalDuration = TimeSpan.Zero;
                    foreach (var audio in audios)
                    {
                        var audioId = (audios.Length == 1) ? "all" : indexAudio++.ToString("D5");
                        var tempFile = context.GetPotentialVerboseFilePath($"{transcription.Id}_{audioId}.wav", processStartTime);
                        tempFiles.Add(tempFile);
                        context.FfmpegAudioHelper.ConvertPcmAudioToOtherFormat(audio, tempFile);
                        totalDuration += audio.Duration;
                    }

                    // Construct command-line arguments for transcription
                    var arguments = new StringBuilder();
                    arguments.Append($" --model {this.Model}");
                    if (transcription.Language != null)
                        arguments.Append($" --language {transcription.Language.ShortName}");
                    arguments.Append($" --task transcribe");
                    arguments.Append($" --output_format json");
                    arguments.Append($" {this.AdditionalParameters}");
                    arguments.Append($" --batch_recursive");
                    arguments.Append($" --print_progress");
                    arguments.Append($" --beep_off");
                    arguments.Append($" \"{context.GetPotentialVerboseFilePath($"*.wav", processStartTime)}\"");

                    // Start a new process to perform transcription
                    var process = new Process();
                    process.StartInfo.FileName = this.ApplicationFullPath;
                    process.StartInfo.Arguments = arguments.ToString();
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.UseShellExecute = false;

                    var logs = new StringBuilder();
                    var stopwatch = Stopwatch.StartNew();
                    var errorMessageIfJsonMissing = new StringBuilder();
                    errorMessageIfJsonMissing.AppendLine("Command:");
                    errorMessageIfJsonMissing.AppendLine($"{process.StartInfo.FileName} {process.StartInfo.Arguments}");
                    errorMessageIfJsonMissing.AppendLine();
                    errorMessageIfJsonMissing.AppendLine("Output:");
                    int currentFileIndex = 0;
                    void dataHandler(object s, DataReceivedEventArgs e)
                    {
                        logs.AppendLine(e.Data);
                        errorMessageIfJsonMissing.AppendLine(e.Data);

                        var match = Regex.Match(e.Data ?? string.Empty, "Starting.*:.*-(?<Index>\\d+).wav$", RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            currentFileIndex = int.Parse(match.Groups["Index"].Value) + 1;
                        }

                        context.DefaultProgressUpdateHandler(
                            ToolName,
                            audios.Length == 1
                            ? "all"
                            : $"{currentFileIndex}/{audios.Length}",
                            e.Data);
                    }
                    process.ErrorDataReceived += dataHandler;
                    process.OutputDataReceived += dataHandler;
                    process.Start();
                    process.BeginErrorReadLine();
                    process.BeginOutputReadLine();
                    process.WaitForExit();

                    transcription.Costs.Add(
                        new Cost(
                            ToolName,
                            stopwatch.Elapsed,
                            audios.Length,
                            itemsDuration: totalDuration));

                    // Process transcription results for each temporary audio file
                    for (int i = 0; i < tempFiles.Count; i++)
                    {
                        string tempFile = tempFiles[i];
                        var pcmAudio = audios[i];

                        // Read and parse the JSON file containing transcription results
                        var jsonFilename = Path.ChangeExtension(tempFile, ".json");
                        if (!File.Exists(jsonFilename))
                        {
                            throw new Exception($"Whisper did not create needed file: {jsonFilename}\n\n{errorMessageIfJsonMissing}");
                        }

                        using (var reader = File.OpenText(jsonFilename))
                        using (var jsonReader = new JsonTextReader(reader))
                        {
                            JsonSerializer serializer = new JsonSerializer();
                            var content = serializer.Deserialize<dynamic>(jsonReader);
                            foreach (var segment in content.segments)
                            {
                                var words = new List<TranscribedWord>();
                                foreach (var word in segment.words)
                                {
                                    words.Add(
                                        new TranscribedWord(
                                            pcmAudio.Offset + TimeSpan.FromSeconds((double)word.start),
                                            pcmAudio.Offset + TimeSpan.FromSeconds((double)word.end),
                                            (string)word.word,
                                            (double)word.probability));
                                }

                                // Process and segment the transcribed text based on punctuation marks
                                TimeSpan? currentStartTime = null;
                                string currentText = null;
                                var currentWords = new List<TranscribedWord>();

                                for (int indexWord = 0; indexWord < words.Count; indexWord++)
                                {
                                    var word = words[indexWord];

                                    currentStartTime ??= word.StartTime;
                                    currentText = (currentText ?? string.Empty) + word.Text;
                                    currentWords.Add(word);

                                    // Check for punctuation marks or if its the last word in the segment to create segments.
                                    if (indexWord == words.Count - 1 ||
                                        (this.ForceSplitOnComma && (currentText.EndsWith("\u3001") || currentText.EndsWith(",")) /* , */) ||
                                        currentText.EndsWith("\u3002") /* . */ ||
                                        currentText.EndsWith(".") ||
                                        currentText.EndsWith("?") ||
                                        currentText.EndsWith("!"))
                                    {
                                        transcribedItems.Add(
                                            new TranscribedItem(
                                                currentStartTime.Value,
                                                word.EndTime,
                                                MetadataCollection.CreateSimple(metadataProduced, currentText),
                                                noSpeechProbability: (double)segment.no_speech_prob,
                                                words: currentWords));
                                        currentStartTime = null;
                                        currentText = null;
                                        currentWords.Clear();
                                    }
                                }

                                if (currentStartTime != null)
                                {
                                    throw new Exception("BUG");
                                }
                            }
                        }
                    }

                    var subtitleFile = new SubtitleFile();
                    subtitleFile.Subtitles.AddRange(
                        transcription.Items
                        .Where(f => f.Duration >= this.IgnoreSubtitleShorterThen)
                        .Select(f => new Subtitle(f.StartTime, f.EndTime, f.Metadata.Get(metadataProduced))));
                    subtitleFile.SaveSrt(fullSrtTempFile);

                    // Add the text only when we are sure that everything is working
                    return transcribedItems.ToArray();
                }
                finally
                {
                    if (!context.IsVerbose)
                    {
                        File.Delete(fullSrtTempFile);

                        foreach (var tempFile in tempFiles)
                        {
                            File.Delete(tempFile);
                            File.Delete(Path.ChangeExtension(tempFile, ".json"));
                        }
                    }
                }
            }
        }
    }
}