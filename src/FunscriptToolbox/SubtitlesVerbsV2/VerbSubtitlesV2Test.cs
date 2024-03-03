using CommandLine;
using FunscriptToolbox.Core;
using log4net;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FunscriptToolbox.SubtitlesVerbV2
{
    [JsonObject(IsReference = false)]
    class VerbSubtitlesV2Video2Test : VerbSubtitlesV2
    {
        private static readonly ILog rs_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        [Verb("subtitlesv2.video2test", aliases: new[] { "subv2.vid2test" }, HelpText = "Extract an dummy .srt from video, using Voice Activity Detection")]
        public class Options : OptionsBase
        {
            [Value(0, MetaName = "files", Required = true, HelpText = ".mp4 files")]
            public IEnumerable<string> Input { get; set; }

            [Option('s', "suffix", Required = false, HelpText = "Suffix for the files produced", Default = "")]
            public string Suffix { get; set; }

            [Option('r', "recursive", Required = false, HelpText = "If a file contains '*', allow to search recursivly for matches", Default = false)]
            public bool Recursive { get; set; }

            [Option('p', "extractionparameters", Required = false, HelpText = "Added parameters to pass to ffmpeg when extracting wav")]
            public string ExtractionParameters { get; set; }

            [Option('v', "skipvad", Required = false, HelpText = "Don't use Voice Activity Detection, create an empty .vad file instead", Default = false)]
            public bool SkipVAD { get; set; }

            [Option("silerovad", Required = false, HelpText = "", Default = false)]
            public bool SileroVAD { get; set; }

            [Option("importvad", Required = false, HelpText = "")]
            public string ImportVAD { get; set; }

            [Option("transcribe", Required = false, HelpText = "")]
            public string Transcribe { get; set; }

            [Option("sourcelanguage", Required = false, HelpText = "")]
            public string SourceLanguage { get; set; }

            [Option("destinationlanguage", Required = false, HelpText = "", Default = "en")]
            public string DestinationLanguage { get; set; }
        }

        private readonly Options r_options;

        public VerbSubtitlesV2Video2Test(Options options)
            : base(rs_log, options)
        {
            r_options = options;
        }

        public int Execute()
        {
            return Task.Run<int>(() => ExecuteAsync()).Result;
        }


        public async Task<int> ExecuteAsync()
        {
            foreach (var inputMp4Fullpath in r_options
                .Input
                .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive))
                .Distinct()
                .OrderBy(f => f))
            {
                try
                {
                    var watch = Stopwatch.StartNew();
                    var ffmpegAudioHelper = new FfmpegAudioHelper();
                    var whisperHelper = new WhisperHelper(ffmpegAudioHelper, @"Purfview-Whisper-Faster\whisper-faster.exe");

                    var wipsubFullpath = Path.ChangeExtension(
                        inputMp4Fullpath,
                        r_options.Suffix + WorkInProgressSubtitles.Extension);

                    var wipsub = File.Exists(wipsubFullpath) 
                        ? WorkInProgressSubtitles.FromFile(wipsubFullpath) 
                        : new WorkInProgressSubtitles(wipsubFullpath);

                    wipsub.PcmAudio ??= ffmpegAudioHelper.ExtractPcmAudio(inputMp4Fullpath, extractionParameters: r_options.ExtractionParameters);
                    wipsub.Save();

                    wipsub.VoiceDetectionFile ??= ImportVad(inputMp4Fullpath);
                    wipsub.Save();

                    // if (DoFull)
                    wipsub.Transcriptions.AddIfMissing(
                        "f",
                        () => new Transcription(
                            whisperHelper.TranscribeAudio(
                                new[] { wipsub.PcmAudio }, 
                                language: r_options.SourceLanguage)));
                    wipsub.Save();

                    wipsub.Transcriptions.AddIfMissing(
                        "sv",
                        () => new Transcription(
                                whisperHelper.TranscribeAudio(
                                    wipsub.VoiceDetectionFile.Select(
                                        vad => wipsub.PcmAudio.ExtractSnippet(vad.Start, vad.End))
                                    .ToArray(),
                                    language: r_options.SourceLanguage)));
                    wipsub.Save();

                    var translator = new GoogleTranslate();

                    foreach (var transcribedText in wipsub.Transcriptions.Values.SelectMany(f => f))
                    {
                        transcribedText.Translation ??= translator.Translate(transcribedText.Text, r_options.SourceLanguage, "en");
                    }

                    wipsub.Save();

                    WriteInfo($"{inputMp4Fullpath}: Finished in {watch.Elapsed}.");
                    WriteInfo();
                }
                catch (Exception ex)
                {
                    WriteError($"{inputMp4Fullpath}: Exception occured: {ex}");
                    WriteInfo();
                }
            }

            return base.NbErrors;
        }

        private VoiceAudioDetectionCollection ImportVad(string inputMp4Fullpath)
        {
            var vadFilePath = Path.ChangeExtension(
                inputMp4Fullpath,
                r_options.ImportVAD);
            if (File.Exists(vadFilePath))
            {
                WriteInfo($"{inputMp4Fullpath}: Importing human VAD file '{r_options.ImportVAD}'...");
                var humanVadSrtFile = SubtitleFile.FromSrtFile(
                    vadFilePath);
                return new VoiceAudioDetectionCollection(
                    humanVadSrtFile.Subtitles.Select(
                        subtitle => VoiceAudioDetection.FromText(
                            subtitle.StartTime, 
                            subtitle.EndTime, 
                            subtitle.Text)));
            }
            else
            {
                return null;
            }
        }


        //public async Task<int> ExecuteAsyncOld()
        //{
        //    foreach (var inputMp4Fullpath in r_options
        //        .Input
        //        .SelectMany(file => HandleStarAndRecusivity(file, r_options.Recursive))
        //        .Distinct()
        //        .OrderBy(f => f))
        //    {
        //        try
        //        {
        //            var wipsubFullpath = Path.ChangeExtension(
        //                inputMp4Fullpath,
        //                r_options.Suffix + WorkInProgressSubtitles.Extension);

        //            var suffixIndex = 20;
        //            do
        //            {
        //                r_options.Suffix = $".{suffixIndex++:D3}";
        //                wipsubFullpath = Path.ChangeExtension(
        //                    inputMp4Fullpath,
        //                    r_options.Suffix + WorkInProgressSubtitles.Extension);
        //            } while (File.Exists(wipsubFullpath));

        //            var watch = Stopwatch.StartNew();
        //            WorkInProgressSubtitles wipsub;
        //            if (File.Exists(wipsubFullpath))
        //            {
        //                wipsub = WorkInProgressSubtitles.FromFile(wipsubFullpath);
        //            }
        //            else
        //            {
        //                wipsub = new WorkInProgressSubtitles(wipsubFullpath);
        //            }

        //            if (wipsub.WavData == null || wipsub.PcmData == null)
        //            {
        //                WriteInfo($"{inputMp4Fullpath}: Extracting .wav file from video...");
        //                ConvertVideoToWavAndPcmData(
        //                    inputMp4Fullpath,
        //                    r_options.ExtractionParameters,
        //                    out var wavData,
        //                    out var pcmData);
        //                wipsub.WavData = wavData;
        //                wipsub.PcmData = pcmData;
        //                wipsub.PcmSamplingRate = VerbSubtitlesV2.SamplingRate;
        //                wipsub.Save();
        //            }

        //            if (r_options.SileroVAD && wipsub.Sections == null)
        //            {
        //                WriteInfo($"{inputMp4Fullpath}: Creating an empty vad subtitle file...");
        //                wipsub.Sections = ExpandSubtitleSections(ExtractSubtitleTimingWithVADFromWavData(wipsub.WavData))
        //                    .ToArray();
        //                wipsub.Save();
        //            }

        //            if (r_options.ImportVAD != null && wipsub.Sections == null)
        //            {
        //                var humanVadFilePath = Path.ChangeExtension(
        //                    inputMp4Fullpath,
        //                    r_options.ImportVAD);
        //                if (File.Exists(humanVadFilePath))
        //                {
        //                    WriteInfo($"{inputMp4Fullpath}: Importing human VAD file '{r_options.ImportVAD}'...");
        //                    var humanVadSrtFile = SubtitleFile.FromSrtFile(
        //                        humanVadFilePath);
        //                    wipsub.Sections = ExpandSubtitleSections(humanVadSrtFile.Subtitles)
        //                        .ToArray();
        //                    wipsub.Save();
        //                }
        //            }

        //            if (r_options.Transcribe != null && wipsub.Sections != null && !(wipsub.Sections.FirstOrDefault()?.Results?.Count > 0))
        //            {
        //                var silenceGapSamples = new byte[GetNbSamplesFromDuration(TimeSpan.FromSeconds(0.3))];

        //                foreach (var whisperConfigString in r_options.Transcribe.Split(','))
        //                {
        //                    var tempWavFullPath = Path.GetTempFileName() + ".wav";
        //                    var splits = whisperConfigString.Split('/');
        //                    var modelEnum = (GgmlType)Enum.Parse(typeof(GgmlType), splits[0]);
        //                    var numberOfIteration = int.Parse(splits.Skip(1).FirstOrDefault() ?? "1");

        //                    for (var iteration = 0; iteration < numberOfIteration; iteration++)
        //                    {
        //                        var whisperConfig = new WhisperConfig(modelEnum, iteration);
        //                        // Temperature?, Seed?
        //                        wipsub.WhisperConfigs.Add(whisperConfig);

        //                        if (!File.Exists(whisperConfig.ModelName))
        //                        {
        //                            WriteInfo($"{inputMp4Fullpath}: Downloading model '{whisperConfig.ModelEnum}', saving as '{whisperConfig.ModelName}'...");
        //                            using (var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(whisperConfig.ModelEnum))
        //                            using (var fileWriter = File.OpenWrite(whisperConfig.ModelName))
        //                            {
        //                                await modelStream.CopyToAsync(fileWriter);
        //                            }
        //                        }

        //                        using (var whisperFactory = WhisperFactory.FromPath(whisperConfig.ModelName))
        //                        using (var processor = whisperFactory.CreateBuilder()
        //                                //.WithLanguage("ja")
        //                                .WithTokenTimestamps()
        //                                .WithProbabilities()
        //                                .Build())
        //                        {
        //                            var i = 0;
        //                            var watchA = Stopwatch.StartNew();
        //                            var watchB = Stopwatch.StartNew();

        //                            var nbToDo = 150;
        //                            var maxContext = 0;

        //                            var context = new List<string>();

        //                            foreach (var section in wipsub.Sections)
        //                            {
        //                                Console.WriteLine($"{whisperConfig.ModelEnum}-{whisperConfig.Iteration}  {i++}/{wipsub.Sections.Length}  {section.StartTime}");
        //                                if (section.Lines.Any(f => f.Contains("SCREENCAPTURE")))
        //                                {
        //                                    Console.WriteLine("SKIP SCREEN CAPTURE");
        //                                    continue;
        //                                }
        //                                if (section.NbSourceSubtitles > 1)
        //                                {
        //                                    Console.WriteLine("SKIP NbSourceSubtitles > 1");
        //                                    continue;
        //                                }

        //                                var duration = section.EndTime - section.StartTime;

        //                                var startIndex = GetNbSamplesFromDuration(section.StartTime);
        //                                var durationIndex = GetNbSamplesFromDuration(duration);
        //                                var endIndex = startIndex + durationIndex;

        //                                using var memoryStream = new MemoryStream();
        //                                memoryStream.Write(
        //                                        wipsub.PcmData,
        //                                        startIndex,
        //                                        (endIndex > wipsub.PcmData.Length)
        //                                            ? wipsub.PcmData.Length - startIndex
        //                                            : durationIndex);
        //                                memoryStream.Write(silenceGapSamples, 0, silenceGapSamples.Length);
        //                                var pcmData = memoryStream.ToArray();

        //                                watchA.Start();
        //                                ConvertPcmDataToWav(
        //                                    tempWavFullPath,
        //                                    pcmData,
        //                                    0,
        //                                    pcmData.Length);
        //                                ConvertPcmDataToWav(
        //                                    $"{wipsubFullpath}.{i:D4}.wav",
        //                                    pcmData,
        //                                    0,
        //                                    pcmData.Length);
        //                                watchA.Stop();
        //                                watchB.Start();

        //                                var prompt = string.Join(Environment.NewLine, context);
        //                                //using var whisperFactoryB = WhisperFactory.FromPath(whisperConfig.ModelName);
        //                                using var processorB = whisperFactory.CreateBuilder()
        //                                        //.WithLanguage("ja")
        //                                        .WithTokenTimestamps()
        //                                        .WithProbabilities()
        //                                        .WithPrompt(prompt)
        //                                        .Build();

        //                                using (var fileStream = File.OpenRead(tempWavFullPath))
        //                                {
        //                                    var results = new WhisperResult(whisperConfig);
        //                                    await foreach (var result in processorB.ProcessAsync(fileStream))
        //                                    {
        //                                        results.TranscribedSegments.Add(result);
        //                                        context.Add(result.Text);
        //                                        if (context.Count > maxContext)
        //                                        {
        //                                            context.RemoveAt(0);
        //                                        }
        //                                    }
        //                                    section.Results.Add(results);
        //                                }
        //                                watchB.Stop();
        //                                if (--nbToDo == 0)
        //                                {
        //                                    break;
        //                                }
        //                            }
        //                            wipsub.Save();
        //                            Console.WriteLine($"{whisperConfig.ModelEnum}-{whisperConfig.Iteration} {watchA.Elapsed},{watchB.Elapsed}");
        //                        }

        //                        var tempMergedPcmAFullpath = Path.GetTempFileName() + ".mergedA.pcm";
        //                        var tempMergedPcmBFullpath = Path.GetTempFileName() + ".mergedB.pcm";
        //                        using (var whisperFactory = WhisperFactory.FromPath(whisperConfig.ModelName))
        //                        using (var processor = whisperFactory.CreateBuilder()
        //                                //.WithLanguage("ja")
        //                                .WithTokenTimestamps()
        //                                .WithProbabilities()
        //                                .Build())
        //                        using (var pcmWriterA = File.Create(tempMergedPcmAFullpath))
        //                        using (var pcmWriterB = File.Create(tempMergedPcmBFullpath))
        //                        {
        //                            var combinedDurationA = TimeSpan.Zero;
        //                            var combinedDurationB = TimeSpan.Zero;
        //                            var audioOffsetsA = new List<AudioOffset>();
        //                            var audioOffsetsB = new List<AudioOffset>();
        //                            var nbBlock = 0;
        //                            var blockSize = TimeSpan.Zero;
        //                            var nbSubtitleInBloc = 0;
        //                            for (int index = 0; index < wipsub.Sections.Length; index++)
        //                            {
        //                                var section = wipsub.Sections[index];
        //                                var nextSection = index + 1 < wipsub.Sections.Length ? wipsub.Sections[index + 1] : null;
        //                                if (section.Lines.Any(f => f.Contains("SCREENCAPTURE")))
        //                                {
        //                                    Console.WriteLine("SKIP SCREEN CAPTURE");
        //                                    continue;
        //                                }

        //                                audioOffsetsA.Add(
        //                                    new AudioOffset(
        //                                        combinedDurationA,
        //                                        combinedDurationA + (section.EndTime - section.StartTime),
        //                                        section.StartTime - combinedDurationA));
        //                                audioOffsetsB.Add(
        //                                    new AudioOffset(
        //                                        combinedDurationB,
        //                                        combinedDurationB + (section.EndTime - section.StartTime),
        //                                        section.StartTime - combinedDurationB));

        //                                nbSubtitleInBloc++;
        //                                var duration = section.EndTime - section.StartTime;
        //                                var startIndex = GetNbSamplesFromDuration(section.StartTime);
        //                                var durationIndex = GetNbSamplesFromDuration(duration);
        //                                var endIndex = startIndex + durationIndex;
        //                                pcmWriterA.Write(
        //                                    wipsub.PcmData,
        //                                    startIndex,
        //                                    (endIndex > wipsub.PcmData.Length) ? wipsub.PcmData.Length - startIndex : endIndex - startIndex);
        //                                pcmWriterB.Write(
        //                                    wipsub.PcmData,
        //                                    startIndex,
        //                                    (endIndex > wipsub.PcmData.Length) ? wipsub.PcmData.Length - startIndex : endIndex - startIndex);
        //                                combinedDurationA += duration;
        //                                combinedDurationB += duration;
        //                                blockSize += duration;

        //                                var gapDuration = TimeSpan.FromSeconds(0.3);
        //                                pcmWriterA.Write(silenceGapSamples, 0, GetNbSamplesFromDuration(gapDuration));
        //                                combinedDurationA += gapDuration;

        //                                if (nextSection?.StartTime > section.EndTime && nextSection.StartTime - section.EndTime < TimeSpan.FromSeconds(0.5))
        //                                {
        //                                    var si = GetNbSamplesFromDuration(section.EndTime);
        //                                    pcmWriterB.Write(
        //                                        wipsub.PcmData,
        //                                        si,
        //                                        GetNbSamplesFromDuration(nextSection.StartTime) - si);
        //                                    combinedDurationB += nextSection.StartTime - section.EndTime;
        //                                }
        //                                else
        //                                {
        //                                    pcmWriterB.Write(silenceGapSamples, 0, GetNbSamplesFromDuration(gapDuration));
        //                                    combinedDurationB += gapDuration;
        //                                }

        //                                nbBlock++;
        //                                blockSize = TimeSpan.Zero;
        //                                nbSubtitleInBloc = 0;
        //                            }
        //                        }

        //                        using (var whisperFactory = WhisperFactory.FromPath(whisperConfig.ModelName))
        //                        using (var processor = whisperFactory.CreateBuilder()
        //                                //.WithLanguage("ja")
        //                                .WithTokenTimestamps()
        //                                .WithProbabilities()
        //                                .Build())
        //                        using (var writerX = File.CreateText(wipsubFullpath + ".mergeA.txt"))
        //                        {
        //                            ConvertPcmToWav(tempMergedPcmAFullpath, tempWavFullPath);
        //                            ConvertPcmToWav(tempMergedPcmAFullpath, wipsubFullpath + ".mergeA.wav");
        //                            using (var fileStream = File.OpenRead(tempWavFullPath))
        //                            {
        //                                var results = new WhisperResult(whisperConfig);
        //                                await foreach (var result in processor.ProcessAsync(fileStream))
        //                                {
        //                                    results.TranscribedSegments.Add(result);
        //                                    writerX.WriteLine(result.Text);
        //                                }
        //                            }
        //                        }

        //                        using (var whisperFactory = WhisperFactory.FromPath(whisperConfig.ModelName))
        //                        using (var processor = whisperFactory.CreateBuilder()
        //                                //.WithLanguage("ja")
        //                                .WithTokenTimestamps()
        //                                .WithProbabilities()
        //                                .Build())
        //                        using (var writerX = File.CreateText(wipsubFullpath + ".mergeB.txt"))
        //                        {
        //                            var index = 1;
        //                            ConvertPcmToWav(tempMergedPcmBFullpath, tempWavFullPath);
        //                            ConvertPcmToWav(tempMergedPcmBFullpath, wipsubFullpath + ".mergeB.wav");
        //                            using (var fileStream = File.OpenRead(tempWavFullPath))
        //                            {
        //                                var results = new WhisperResult(whisperConfig);
        //                                await foreach (var result in processor.ProcessAsync(fileStream))
        //                                {
        //                                    results.TranscribedSegments.Add(result);
        //                                    writerX.WriteLine($"{index++}");
        //                                    writerX.WriteLine($"{SubtitleFile.FormatTimespan(result.Start)} --> {SubtitleFile.FormatTimespan(result.End)}");
        //                                    writerX.WriteLine(result.Text);
        //                                }
        //                            }
        //                        }
        //                    }
        //                }
        //            }

        //            using (var writer = File.CreateText(Path.ChangeExtension(wipsubFullpath, ".DeepL.txt")))
        //            {
        //                var i = 0;
        //                foreach (var transcribedSegment in wipsub.Sections.SelectMany(f => f.Results).SelectMany(f => f.TranscribedSegments))
        //                {
        //                    writer.WriteLine($"[{i++:D5}] {transcribedSegment.Text}");
        //                }
        //            }

        //            WriteInfo($"{inputMp4Fullpath}: Finished in {watch.Elapsed}.");
        //            WriteInfo();
        //        }
        //        catch (Exception ex)
        //        {
        //            WriteError($"{inputMp4Fullpath}: Exception occured: {ex}");
        //            WriteInfo();
        //        }
        //    }

        //    return base.NbErrors;
        //}

        //private IEnumerable<SubtitleSection> ExpandSubtitleSections(IEnumerable<Subtitle> subtitles)
        //{
        //    foreach (var subtitle in subtitles)
        //    {
        //        yield return new SubtitleSection(subtitle);
        //    }
        //    yield break;

        //    int nbSubtitlesInBlock = 0;
        //    TimeSpan blockStartTime = TimeSpan.MinValue;
        //    TimeSpan blockEndTime = TimeSpan.MinValue;
        //    foreach (var subtitle in subtitles)
        //    {
        //        yield return new SubtitleSection(subtitle);
        //        if (nbSubtitlesInBlock > 0 && subtitle.StartTime - blockEndTime < TimeSpan.FromMilliseconds(500))
        //        {
        //            blockEndTime = subtitle.EndTime;
        //            nbSubtitlesInBlock++;
        //        }
        //        else
        //        {
        //            if (nbSubtitlesInBlock > 1)
        //            {
        //                yield return new SubtitleSection(blockStartTime, blockEndTime, nbSubtitlesInBlock);
        //            }
        //            nbSubtitlesInBlock = 1;
        //            blockStartTime = subtitle.StartTime;
        //            blockEndTime = subtitle.EndTime;
        //        }
        //    }
        //    if (nbSubtitlesInBlock > 1)
        //    {
        //        yield return new SubtitleSection(blockStartTime, blockEndTime, nbSubtitlesInBlock);
        //    }
        //}

        //private static int GetNbSamplesFromDuration(TimeSpan duration)
        //{
        //    var number = (int)(duration.TotalSeconds * SamplingRate * NbBytesPerSampling);
        //    return (number % 2 == 0) ? number : number - 1;
        //}

        //private IEnumerable<Subtitle> ExtractSubtitleTimingWithVADFromWavData(byte[] wavData)
        //{
        //    var tempWavFilepath = Path.GetTempFileName() + ".wav";
        //    try
        //    {
        //        File.WriteAllBytes(tempWavFilepath, wavData);
        //        return ExtractSubtitleTimingWithVAD(tempWavFilepath).ToArray();
        //    }
        //    finally
        //    {
        //        File.Delete(tempWavFilepath);
        //    }
        //}

        //private IEnumerable<Subtitle> ExtractSubtitleTimingWithVAD(string inputFilepath)
        //{
        //    var tempVadFilepath = Path.GetTempFileName() + ".vad.json";
        //    try
        //    {
        //        Process process = new Process();
        //        var scriptMessages = new List<string>();
        //        try
        //        {
        //            var pythonScript = GetApplicationFolder(@"PythonSource\funscripttoolbox-extract-vad.py");

        //            // Start the process
        //            process.StartInfo.FileName = "Python.exe";
        //            process.StartInfo.Arguments = $"{pythonScript} \"{inputFilepath}\" \"{tempVadFilepath}\" {SamplingRate}";
        //            process.StartInfo.UseShellExecute = false;
        //            process.StartInfo.RedirectStandardOutput = true;
        //            process.StartInfo.RedirectStandardError = true;
        //            process.OutputDataReceived += (sender, e) => {
        //                scriptMessages.Add(e.Data);
        //                WriteVerbose($"   [Silerio-VAD O] {e.Data}");
        //            };
        //            process.ErrorDataReceived += (sender, e) => {
        //                scriptMessages.Add(e.Data);
        //                WriteVerbose($"   [Silerio-VAD E] {e.Data}");
        //            };

        //            process.Start();
        //            process.BeginOutputReadLine();
        //            process.BeginErrorReadLine();
        //            process.WaitForExit();
        //            if (process.ExitCode != 0)
        //            {
        //                throw new ApplicationException($"{pythonScript} returned code: {process.ExitCode}");
        //            }
        //        }
        //        catch (Exception)
        //        {
        //            WriteError($"An exception occured while running the following python script: {process.StartInfo.FileName} {process.StartInfo.Arguments}");
        //            WriteError($"    Make sure that 'Python' is installed (https://www.python.org/downloads/) with the 'Add Path' selected.");
        //            WriteError($"    If python is installed, make sure to run the following command in a command prompt:");
        //            WriteError($"    pip3 install torch torchvision torchaudio soundfile IPython --extra-index-url https://download.pytorch.org/whl/cu116");
        //            WriteError();
        //            if (scriptMessages.Count > 0)
        //            {
        //                WriteError("Script output:");
        //                foreach (var message in scriptMessages)
        //                {
        //                    WriteError($"   {message}");
        //                }
        //                WriteError();
        //            }
        //            throw;
        //        }

        //        // Read the file produced
        //        using (var reader = File.OpenText(tempVadFilepath))
        //        using (var jsonReader = new JsonTextReader(reader))
        //        {
        //            var content = Serializer.Deserialize<dynamic>(jsonReader);

        //            foreach (var item in content)
        //            {
        //                var start = (double)item.start / SamplingRate;
        //                var end = (double)item.end / SamplingRate;
        //                yield return new Subtitle(
        //                        TimeSpan.FromSeconds(start),
        //                        TimeSpan.FromSeconds(end),
        //                        ".");
        //            }
        //        }
        //    }
        //    finally
        //    {
        //        File.Delete(tempVadFilepath);
        //    }
        //}
    }
}
