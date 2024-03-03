using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    internal class WhisperHelper
    {
        private readonly IFfmpegAudioHelper r_ffmpegAudioHelper;
        private readonly string r_pathToPurfviewWhisper;

        public WhisperHelper(IFfmpegAudioHelper ffmpegAudioHelper, string pathToPurfviewWhisper)
        {
            r_ffmpegAudioHelper = ffmpegAudioHelper;
            r_pathToPurfviewWhisper = pathToPurfviewWhisper;
        }

        internal TranscribedText[] TranscribeAudio(PcmAudio[] audios, string model = "large-V2", string language = null, string transcribeParameters = null)
        {
            var tempPcmBaseFile = Path.GetTempFileName();
            var tempFiles = new List<string>();
            try
            {
                // Convert each input PCM audio to WAV format and store as temporary files
                var indexAudio = 0;
                foreach (var audio in audios)
                {
                    var tempFile = $"{tempPcmBaseFile}-{indexAudio++:D5}.wav";
                    tempFiles.Add(tempFile);
                    r_ffmpegAudioHelper.ConvertPcmAudioToWavFile(audio, tempFile);
                }

                // Construct command-line arguments for transcription
                var arguments = new StringBuilder();
                arguments.Append($" --model {model}");
                if (!string.IsNullOrEmpty(language))
                    arguments.Append($" --language {language}");
                arguments.Append($" --task transcribe");
                arguments.Append($" --batch_recursive");
                arguments.Append($" --print_progress");
                arguments.Append($" --beep_off");
                arguments.Append($" --output_format json");
                arguments.Append($" {transcribeParameters}");
                arguments.Append($" \"{tempPcmBaseFile}-*.wav\"");

                // Start a new process to perform transcription
                var process = new Process();
                process.StartInfo.FileName = r_pathToPurfviewWhisper;
                process.StartInfo.Arguments = arguments.ToString();
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;

                var stopwatch = Stopwatch.StartNew();
                var errors = new List<string>();
                process.ErrorDataReceived += (s, e) => Console.WriteLine($"[Purfview-Whisper] {e.Data}");
                process.OutputDataReceived += (s, e) => Console.Error.WriteLine($"[Purfview-Whisper] {e.Data}");
                process.Start();
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                process.WaitForExit();

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
                                currentText = (currentText == null) ? word.Text : currentText + word.Text;
                                currentWords.Add(word);
                                // Check for punctuation marks or end of words to create segments, or if its the last word in the segment
                                if (indexWord == words.Count -1 || currentText.EndsWith("\u3001") /* , */ || currentText.EndsWith("\u3002") /* . */ || currentText.EndsWith("?") || currentText.EndsWith("!"))
                                {
                                    var currentDuration = word.EndTime - currentStartTime.Value;
                                    // If a segment is longer than 15 seconds, we rerun whisper on that block.
                                    // Usually, it will be broken down into smaller pieces.
                                    if (pcmAudio.Offset == TimeSpan.Zero && currentDuration > TimeSpan.FromSeconds(15))
                                    {
                                        texts.AddRange(
                                            TranscribeAudio(
                                                new[] { pcmAudio.ExtractSnippet(
                                                    currentStartTime.Value, 
                                                    word.EndTime) },
                                                model,
                                                language,
                                                transcribeParameters));
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