using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Outputs
{
    internal abstract class SubtitleOutput
    {
        [JsonProperty(Order = 1)]
        public bool Enabled { get; set; } = true;
        
        [JsonIgnore()]
        public abstract bool NeedSubtitleForcedTimings { get; }

        public abstract void CreateOutput(
            SubtitleGeneratorContext context,
            WorkInProgressSubtitles wipsub);


        protected string[] CreateFinalOrder(string[] order, IEnumerable<string> allIds)
        {
            if (order == null)
                return allIds.Distinct().ToArray();

            var remainingCandidats = allIds.Distinct().ToList();
            var finalOrder = new List<string>();
            foreach (var id in order)
            {
                if (id == "*")
                {
                    finalOrder.AddRange(remainingCandidats);
                    break;
                }
                else if (remainingCandidats.Contains(id))
                {
                    finalOrder.Add(id);
                    remainingCandidats.Remove(id);
                }
            }

            return finalOrder.ToArray();
        }
    }
}
