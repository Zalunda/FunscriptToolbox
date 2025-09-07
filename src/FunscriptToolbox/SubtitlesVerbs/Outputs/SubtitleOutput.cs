using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.Core;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using FunscriptToolbox.SubtitlesVerbs.Infra;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    public abstract class SubtitleOutput : SubtitleWorker
    {
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;

        [JsonIgnore]
        public virtual string Description => $"{this.GetType().Name.Replace(typeof(SubtitleOutput).Name, string.Empty)}";

        public abstract bool IsPrerequisitesMet(
            SubtitleGeneratorContext context,
            out string reason);

        public abstract void CreateOutput(
            SubtitleGeneratorContext context);

        public override void Execute(
            SubtitleGeneratorContext context)
        {
            if (!this.Enabled)
            {
                return;
            }

            if (!this.IsPrerequisitesMet(context, out var reason))
            {
                context.WriteInfo($"Output '{this.Description}' can't be done yet: {reason}");
                context.WriteInfo();
            }
            else
            {
                context.WriteInfo($"Creating Output '{this.Description}'...");
                this.CreateOutput(context);
                context.WriteInfo($"Finished.");
                context.WriteInfo();
            }
        }

        protected static IEnumerable<Subtitle> GetAdjustedSubtitlesToInject(
            List<Subtitle> currentSubtitles, 
            SubtitleToInject[] subtitlesToInject, 
            TimeSpan audioDuration)
        {
            if (subtitlesToInject == null)
            {
                yield break;
            }

            var minimumStartTime = currentSubtitles.Count == 0 ? audioDuration : currentSubtitles.Min(f => f.StartTime);
            var maximumEndTime = currentSubtitles.Count == 0 ? TimeSpan.Zero : currentSubtitles.Max(f => f.EndTime);

            foreach (var subtitleToInject in subtitlesToInject)
            {
                if (subtitleToInject.Origin == SubtitleToInjectOrigin.Start)
                {
                    yield return new Subtitle(
                            TimeSpanExtensions.Min(subtitleToInject.OffsetTime, minimumStartTime),
                            TimeSpanExtensions.Min(subtitleToInject.OffsetTime + subtitleToInject.Duration, minimumStartTime),
                            subtitleToInject.Lines);
                }
                else
                {
                    var startTime = TimeSpanExtensions.Max(audioDuration + subtitleToInject.OffsetTime, maximumEndTime);
                    yield return new Subtitle(
                            startTime,
                            startTime + subtitleToInject.Duration,
                            subtitleToInject.Lines);
                }
            }
        }
    }
}
