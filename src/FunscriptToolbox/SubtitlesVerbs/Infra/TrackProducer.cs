using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.Inputs;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    /// <summary>
    /// A step that produces a Track (kept in the .wipsubtitles under its Id): the transcribers, the transforms and the imports. What they
    /// have in common is here (the track, the import / export of a '.Worker.&lt;id&gt;.srt', StopOnFirstExecution); a subclass gives the
    /// language of what it produces, and does the work (DoWorkInternal).
    /// </summary>
    public abstract class TrackProducer : SubtitleWorker
    {
        [ConfigDoc("Metadata of this step that the next steps don't receive (ex. \"TimingTrace\", only useful to debug). ',' = several.")]
        [JsonProperty(Order = 6)]
        public string PrivateMetadataNames { get; set; }

        [ConfigDoc("Writes the result with all its metadata in '<video>.Worker.<id>.srt'. A '<video>.Worker.<id>.import.srt' (edited) is imported instead of doing the step " +
            "(the old result is kept as '<id>-OLD').",
            Advice = "Useful to look at the result of a step in a subtitle editor, or to fix it by hand.")]
        [JsonProperty(Order = 7)]
        public bool ExportMetadataSrt { get; set; } = false;

        [ConfigDoc("The first time the step is reached, the scene stops before doing it (to look at the result of the steps before it); the next time, it's done.")]
        [JsonProperty(Order = 8)]
        public bool StopOnFirstExecution { get; set; } = false;

        /// <summary>The language of the text produced.</summary>
        protected abstract Language GetTrackLanguage(SubtitleGeneratorContext context);

        protected abstract string GetMetadataProduced();

        protected abstract void DoWorkInternal(SubtitleGeneratorContext context, Track track);

        protected override void DescribeDataFlow(WorkerDataFlow flow)
        {
            base.DescribeDataFlow(flow);
            flow.Output(DataKind.Track, this.Id);
            if (this.ExportMetadataSrt)
                flow.Output(DataKind.File, $".Worker.{this.Id}.srt");
        }

        protected override bool IsFinished(SubtitleGeneratorContext context)
        {
            return context.WIP.Tracks.Any(t => t.Id == this.Id && t.IsFinished);
        }

        protected override void EnsureDataObjectExists(SubtitleGeneratorContext context)
        {
            if (!context.WIP.Tracks.Any(t => t.Id == this.Id))
            {
                context.WIP.Tracks.Add(new Track(
                    this.Id,
                    this.GetMetadataProduced(),
                    GetPrivateMetadataNamesArray(this.PrivateMetadataNames),
                    GetTrackLanguage(context)));
            }
        }

        protected override void DoWork(SubtitleGeneratorContext context)
        {
            var track = context.WIP.Tracks.FirstOrDefault(t => t.Id == this.Id);
            var importedTrack = TryImportMetadatasSrt(context, track);

            if (importedTrack != null)
            {
                track = importedTrack;
            }
            else if (this.StopOnFirstExecution && !track.HasStoppedOnFirstExecution)
            {
                context.WriteInfo("STOPPING to let user see the result of previous steps.");
                track.HasStoppedOnFirstExecution = true;
                context.WIP.Save();
            }
            else
            {
                DoWorkInternal(context, track);
            }
        }

        private Track TryImportMetadatasSrt(
            SubtitleGeneratorContext context,
            Track track)
        {
            var virtualSubtitleFile = context.WIP.LoadVirtualSubtitleFile(
                $".Worker.{this.Id}.import.srt");
            if (virtualSubtitleFile == null)
            {
                return null;
            }

            RenameOldTrack(context, track);

            var importedTrack = new Track(
                this.Id,
                this.GetMetadataProduced(),
                GetPrivateMetadataNamesArray(this.PrivateMetadataNames),
                GetTrackLanguage(context),
                true,
                ReadMetadataSubtitles(virtualSubtitleFile.Subtitles).
                    Select(f => new TimedItemWithMetadata(f.StartTime, f.EndTime, f.Metadata)));
            context.WIP.Tracks.Add(importedTrack);
            context.WIP.Save();

            return importedTrack;
        }

        private static void RenameOldTrack(SubtitleGeneratorContext context, Track track)
        {
            int i = 2;
            string newId = $"{track.Id}-OLD";
            while (context.WIP.Tracks.Any(t => t.Id == newId))
            {
                newId = $"{track.Id}-OLD{i++}";
            }
            track.ChangeId(newId);
        }

        protected override void AfterWork(SubtitleGeneratorContext context, bool wasAlreadyFinished)
        {
            var track = context.WIP.Tracks.FirstOrDefault(t => t.Id == this.Id);
            if (track != null && this.ExportMetadataSrt)
            {
                DoExportMetatadaSrt(context, track, wasAlreadyFinished);
            }
        }

        protected override IEnumerable<string> GetAdditionalStatusLines(SubtitleGeneratorContext context)
        {
            var track = context.WIP.Tracks.First(t => t.Id == this.Id);

            yield return $"Number of subtitles = {track.Items.Count}";
            yield return $"Total subtitles duration = {track.Items.Sum(f => f.Duration)}";
            yield return $"Language = {track.Language.LongName}";
            foreach (var line in GetForcedTimingsAnalysis(context, track))
            {
                yield return line;
            }
        }

        /// <summary>How the items of the track fit the timings of the first enabled InputImportMetadatas (ex. 'manual-input'), when it's done.</summary>
        private static IEnumerable<string> GetForcedTimingsAnalysis(
            SubtitleGeneratorContext context,
            Track track)
        {
            var firstPerfectVadId = context.Config.Workers.OfType<InputImportMetadatas>().FirstOrDefault(f => f.Enabled)?.Id;
            var timings = context.WIP.Tracks.FirstOrDefault(t => t.Id == firstPerfectVadId && t.IsFinished)?.GetItems();
            if (timings != null && track.MetadataAlwaysProduced != null)
            {
                var nbEmptyItems = track.Items.Count(item => string.IsNullOrWhiteSpace(item.Metadata.Get(track.MetadataAlwaysProduced)));
                var suffixeEmptyItems = nbEmptyItems == 0 ? string.Empty : $" ({nbEmptyItems} are empty)";

                var analysis = track.GetAnalysis(timings);
                yield return $"ForcedTimings Analysis:";
                yield return $"   Number with an item:    {analysis.NbTimingsWithItem}{suffixeEmptyItems}";
                yield return $"   Number without item:    {analysis.TimingsWithoutItem.Length}";
                if (analysis.ExtraItems.Count > 0)
                {
                    yield return $"   Extra items:            {analysis.ExtraItems.Count}";
                }
            }
        }
    }
}
