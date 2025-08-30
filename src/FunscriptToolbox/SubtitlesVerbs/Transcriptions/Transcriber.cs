using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Infra;
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

        [JsonIgnore]
        public virtual bool CanBeUpdated { get; } = false;

        protected Transcriber()
        {
        }

        protected abstract bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason);

        protected abstract void Transcribe(
            SubtitleGeneratorContext context,
            Transcription transcription);

        protected abstract string GetMetadataProduced();

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
                    context.OverrideSourceLanguage,
                    this.GetMetadataProduced());
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
                    context.WriteLog(ex.ToString());
                }
            }
        }

        protected virtual int GetNbEmptyItems(Transcription transcription) => 0;

        private IEnumerable<string> GetTranscriptionAnalysis(
            SubtitleGeneratorContext context,
            Transcription transcription)
        {
            var firstPerfectVadId = context.Config.Workers.OfType<TranscriberPerfectVAD>().FirstOrDefault()?.TranscriptionId;
            var timings = context.CurrentWipsub.Transcriptions.FirstOrDefault(t => t.Id == firstPerfectVadId)?.GetItems();
            if (timings != null )
            {
                var nbEmptyItems = GetNbEmptyItems(transcription);
                var suffixeEmptyItems = nbEmptyItems == 0 ? string.Empty : $" ({nbEmptyItems} are empty)";

                var analysis = transcription.GetAnalysis(timings);
                yield return $"    ForcedTimings Analysis:";
                yield return $"       Number with transcription:    {analysis.NbTimingsWithTranscription}{suffixeEmptyItems}";
                yield return $"       Number without transcription: {analysis.TimingsWithoutItem.Length}";
                if (analysis.ExtraItems.Count > 0)
                {
                    yield return $"       Extra transcriptions:         {analysis.ExtraItems.Count}";
                }
            }
        }
    }
}