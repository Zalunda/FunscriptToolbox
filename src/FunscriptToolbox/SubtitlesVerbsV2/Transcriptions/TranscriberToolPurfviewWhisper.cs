using FunscriptToolbox.SubtitlesVerbV2;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbsV2.Transcriptions
{
    public class TranscriberToolPurfviewWhisper : TranscriberTool
    {
        private object r_lock = new object();

        public TranscriberToolPurfviewWhisper()
        {
        }

        [JsonProperty(Order = 10)]
        public string Model { get; set; } = "Large-V2";
        [JsonProperty(Order = 11)]
        public bool ForceSplitOnComma { get; set; } = true;
        [JsonProperty(Order = 12)]
        public TimeSpan RedoBlockLargerThen { get; set; } = TimeSpan.FromSeconds(15);

        public override TranscribedText[] TranscribeAudio(
            FfmpegAudioHelper audioHelper,
            PcmAudio[] audios,
            Language sourceLanguage,
            out TranscriptionCost[] costs)
        {
            var costsList = new List<TranscriptionCost>();
            var transcribedTexts = TranscribeAudioInternal(
                audioHelper, 
                audios, 
                sourceLanguage, 
                costsList);
            costs = costsList.ToArray();
            return transcribedTexts;

        }

        private TranscribedText[] TranscribeAudioInternal(
            FfmpegAudioHelper audioHelper,
            PcmAudio[] audios,
            Language sourceLanguage,
            List<TranscriptionCost> costs)
        {
            // TODO Add info/progress/verbose logs

            // Only allows one thread doing transption at a times
            lock (r_lock)
            {

                var tempPcmBaseFile = Path.GetTempFileName();
                var tempFiles = new List<string>();
                try
                {
                    // Convert each input PCM audio to WAV format and store as temporary files
                    var indexAudio = 0;
                    var totalDuration = TimeSpan.Zero;
                    foreach (var audio in audios)
                    {
                        var tempFile = $"{tempPcmBaseFile}-{indexAudio++:D5}.wav";
                        tempFiles.Add(tempFile);
                        audioHelper.ConvertPcmAudioToWavFile(audio, tempFile);
                        totalDuration += audio.Duration;
                    }

                    // Construct command-line arguments for transcription
                    var arguments = new StringBuilder();
                    arguments.Append($" --model {this.Model}");
                    if (sourceLanguage != null)
                        arguments.Append($" --language {sourceLanguage.ShortName}");
                    arguments.Append($" --task transcribe");
                    arguments.Append($" --batch_recursive");
                    arguments.Append($" --print_progress");
                    arguments.Append($" --beep_off");
                    arguments.Append($" --output_format json");
                    arguments.Append($" {this.AdditionalParameters}");
                    arguments.Append($" \"{tempPcmBaseFile}-*.wav\"");

                    // Start a new process to perform transcription
                    var process = new Process();
                    process.StartInfo.FileName = this.ApplicationFullPath;
                    process.StartInfo.Arguments = arguments.ToString();
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.UseShellExecute = false;

                    var logs = new StringBuilder();
                    var stopwatch = Stopwatch.StartNew();
                    var errors = new List<string>();
                    void dataHandler(object s, DataReceivedEventArgs e)
                    {
                        logs.AppendLine(e.Data);
                        Console.WriteLine($"[Purfview-Whisper] {e.Data}");
                    }
                    process.ErrorDataReceived += dataHandler;
                    process.OutputDataReceived += dataHandler;
                    process.Start();
                    process.BeginErrorReadLine();
                    process.BeginOutputReadLine();
                    process.WaitForExit();

                    costs.Add(
                        new TranscriptionCost(
                            "Purview-Whisper",
                            stopwatch.Elapsed,
                            audios.Length,
                            totalDuration));

                    // Process transcription results for each temporary audio file
                    var texts = new List<TranscribedText>();
                    for (int i = 0; i < tempFiles.Count; i++)
                    {
                        string tempFile = tempFiles[i];
                        var pcmAudio = audios[i];

                        // Read and parse the JSON file containing transcription results
                        var jsonFilename = Path.ChangeExtension(tempFile, ".json");
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
                                        (this.ForceSplitOnComma && currentText.EndsWith("\u3001") /* , */) ||
                                        currentText.EndsWith("\u3002") /* . */ ||
                                        currentText.EndsWith("?") ||
                                        currentText.EndsWith("!"))
                                    {
                                        // If a segment is longer than the configured duration, we rerun whisper on that block.
                                        // Usually, it will be broken down into smaller pieces.
                                        var currentDuration = word.EndTime - currentStartTime.Value;
                                        if (pcmAudio.Offset == TimeSpan.Zero && currentDuration > this.RedoBlockLargerThen)
                                        {
                                            texts.AddRange(
                                                TranscribeAudioInternal(
                                                    audioHelper,
                                                    new[] { 
                                                        pcmAudio.ExtractSnippet(
                                                            currentStartTime.Value,
                                                            word.EndTime) },
                                                    sourceLanguage,
                                                    costs));
                                        }
                                        else
                                        {
                                            texts.Add(
                                               new TranscribedText(
                                                   currentStartTime.Value,
                                                   word.EndTime,
                                                   currentText,
                                                   (double)segment.no_speech_prob,
                                                   currentWords));
                                        }
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

                    return texts.ToArray();
                }
                finally
                {
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