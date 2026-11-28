using FunscriptToolbox.SubtitlesVerbs.Infra;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Processors
{
    /// <summary>
    /// Another name for a transcription / translation (ex. one name for the transcription that a workflow chose), so that the
    /// steps after it always use the same name, whatever the workflow. Nothing is copied, nothing is saved in the .wipsubtitles:
    /// the name is resolved each time it's used (see <see cref="Resolve"/>), so it always gives the current state of its source.
    /// (Unlike <see cref="TransformClone"/>: a copy, frozen once done, ex. 'timings', so that the next steps keep the same timings
    /// even if its source changes.)
    /// It's not a task: it's not run, it's not in the task tree, it has no box in the 'Flow' view (the steps that use it are linked to its source).
    /// An alias can be the source of another alias. A disabled alias doesn't exist.
    /// </summary>
    public class TrackAlias : SubtitleWorker
    {
        [JsonProperty(Order = 10, Required = Required.Always)]
        public string SourceId { get; set; }

        internal override bool IsTask => false;

        protected override string GetDefaultProduces() => "Alias";
        protected override string GetDefaultVerb() => "Resolving";

        // (nothing is produced: the steps that use the alias are linked to its source, see SceneFlowGraph)
        protected override void DescribeDataFlow(WorkerDataFlow flow)
        {
        }

        protected override bool IsPrerequisitesMet(SubtitleGeneratorContext context, out string reason)
        {
            reason = null;
            return true;
        }

        protected override bool IsFinished(SubtitleGeneratorContext context) => true;
        protected override void EnsureDataObjectExists(SubtitleGeneratorContext context) { }
        protected override void DoWork(SubtitleGeneratorContext context) { }

        /// <summary>The enabled aliases of a configuration.</summary>
        internal static TrackAlias[] GetAliases(SubtitleGeneratorConfig config)
            => (config?.Workers ?? Array.Empty<SubtitleWorker>()).OfType<TrackAlias>().Where(a => a.Enabled).ToArray();

        /// <summary>
        /// The id of the transcription / translation that an id really is: the source of the alias with this id (following the aliases
        /// of aliases), or the id itself when it's not an alias.
        /// </summary>
        /// <exception cref="InvalidOperationException">The aliases loop (ex. 'a' => 'b' => 'a').</exception>
        internal static string Resolve(SubtitleGeneratorConfig config, string id)
        {
            var aliases = GetAliases(config);
            var seen = new List<string>();
            var current = id;
            while (true)
            {
                var alias = aliases.FirstOrDefault(a => a.Id == current);
                if (alias == null)
                    return current;
                if (seen.Contains(current))
                    throw new InvalidOperationException($"The aliases (TrackAlias) make a loop: {string.Join(" => ", seen)} => {current}.");
                seen.Add(current);
                current = alias.SourceId;
            }
        }
    }
}
