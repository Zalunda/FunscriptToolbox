using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Infra;
using FunscriptToolbox.SubtitlesVerbs.Translations;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    public abstract class Transcriber : SubtitleWorker
    {
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        [JsonProperty(Order = 2, Required = Required.Always)]
        public string TranscriptionId { get; set; }

        [JsonProperty(Order = 100, TypeNameHandling = TypeNameHandling.None)]
        public Translator[] Translators { get; set; }

        [JsonIgnore]
        public virtual bool CanBeUpdated { get; } = false;

        public Transcriber()
        {
        }

        protected abstract bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason);

        protected abstract void Transcribe(
            SubtitleGeneratorContext context,
            Transcription transcription);

        public override void Execute(
            SubtitleGeneratorContext context)
        {
            if (!this.Enabled)
            {
                return;
            }

            var transcription = context.CurrentWipsub.Transcriptions.FirstOrDefault(
                t => t.Id == this.TranscriptionId);
            if (transcription == null)
            {
                transcription = new Transcription(
                    this.TranscriptionId,
                    context.OverrideSourceLanguage);
                context.CurrentWipsub.Transcriptions.Add(transcription);
            }

            if (transcription.IsFinished && !this.CanBeUpdated)
            {
                context.WriteInfoAlreadyDone($"Transcription '{this.TranscriptionId}' have already been done:");
                context.WriteInfoAlreadyDone($"    Number of subtitles = {transcription.Items.Count}");
                context.WriteInfoAlreadyDone($"    Total subtitles duration = {transcription.Items.Sum(f => f.Duration)}");
                context.WriteInfoAlreadyDone($"    Detected Language = {transcription.Language.LongName}");

                foreach (var line in GetTranscriptionAnalysis(context, transcription))
                {
                    context.WriteInfoAlreadyDone(line);
                }
                context.WriteInfoAlreadyDone();
            }
            else if (!this.IsPrerequisitesMet(context, out var reason))
            {
                context.WriteInfo($"Transcription '{this.TranscriptionId}' can't be done yet: {reason}");
                context.WriteInfo();
            }
            else
            {
                try
                {
                    var watch = Stopwatch.StartNew();
                    context.WriteInfo($"Transcribing '{this.TranscriptionId}'...");
                    this.Transcribe(context, transcription);

                    if (transcription.IsFinished)
                    {
                        context.WriteInfo($"Finished in {watch.Elapsed}:");
                        context.WriteInfo($"    Number of subtitles = {transcription.Items.Count}");
                        context.WriteInfo($"    Total subtitles duration = {transcription.Items.Sum(f => f.Duration)}");
                        context.WriteInfo($"    Detected Language = {transcription.Language.LongName}");
                        foreach (var line in GetTranscriptionAnalysis(context, transcription))
                        {
                            context.WriteInfo(line);
                        }
                    }
                    else
                    {
                        context.WriteInfo($"Not finished yet in {watch.Elapsed}.");
                    }
                    context.WriteInfo();

                    context.CurrentWipsub.Save();
                }
                catch (TranscriberNotReadyException ex)
                {
                    context.WriteInfo($"Transcription '{this.TranscriptionId}' can't be done yet: {ex.Reason}");
                    context.WriteInfo();
                    foreach (var userTodo in ex.UserTodos)
                    {
                        context.AddUserTodo(userTodo);
                    }
                }
                catch (Exception ex)
                {
                    context.WriteError($"An error occured while transcribing '{this.TranscriptionId}':\n{ex.Message}");
                }
            }
        }


        private static IEnumerable<string> GetTranscriptionAnalysis(
            SubtitleGeneratorContext context,
            Transcription transcription)
        {
            yield break;
            //var analysis = transcription.GetAnalysis(context);
            //if (analysis != null)
            //{
            //    yield return $"    ForcedTimings Analysis:";
            //    yield return $"       Number with transcription:    {analysis.NbTimingsWithTranscription}";
            //    yield return $"       Number without transcription: {analysis.TimingsWithoutTranscription.Length}";
            //    if (analysis.ExtraTranscriptions.Length > 0)
            //    {
            //        yield return $"       Extra transcriptions:  {analysis.ExtraTranscriptions.Length}";
            //    }

            //    if (context.IsVerbose)
            //    {
            //        using (var writer = File.CreateText(context.GetPotentialVerboseFilePath($"{transcription.Id}-ANALYSIS-versus-ForcedTimings.txt")))
            //        {
            //            var extraTranscriptions = analysis.ExtraTranscriptions.ToList();
            //            foreach (var item in analysis
            //                .TimingsWithOverlapTranscribedTexts
            //                .OrderBy(f => f.Key.StartTime))
            //            {
            //                while (extraTranscriptions.FirstOrDefault()?.StartTime <= item.Key.StartTime)
            //                {
            //                    var extra = extraTranscriptions.First();
            //                    writer.WriteLine($"[Extra transcription, don't overlap with forced timings] {extra.StartTime} => {extra.EndTime}, {extra.GetFirstTranslatedIfPossible()}");
            //                    extraTranscriptions.RemoveAt(0);
            //                }

            //                if (item.Value.Length == 0)
            //                {
            //                    writer.WriteLine($"[No transcription found] {item.Key.StartTime} => {item.Key.EndTime}");
            //                }
            //                else if (item.Value.Length == 1)
            //                {
            //                    var first = item.Value.First();
            //                    writer.WriteLine($"{item.Key.StartTime} => {item.Key.EndTime}, {first.TranscribedText.GetFirstTranslatedIfPossible()}");
            //                }
            //                else
            //                {
            //                    writer.WriteLine($"{item.Key.StartTime} => {item.Key.EndTime}");
            //                    foreach (var value in item.Value)
            //                    {
            //                        var tt = value.TranscribedText;
            //                        writer.WriteLine($"     {tt.StartTime} => {tt.EndTime}, {tt.GetFirstTranslatedIfPossible()}");
            //                    }
            //                }
            //            }
            //        }
            //    }
            //}
        }
    }
}