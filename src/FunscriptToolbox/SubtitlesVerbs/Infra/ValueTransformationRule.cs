using Newtonsoft.Json;
using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class ValueTransformationRule
    {
        [ConfigDoc("A regular expression on the value (not case sensitive; multi-line: '^' and '$' are the start / end of each line).")]
        [JsonProperty(Order = 1, Required = Required.Always)]
        public string Pattern { get; set; }
        // (Required.Always: null or missing is a configuration error when the file is read; Regex.Replace can't take null)
        [ConfigDoc("What each match is replaced by ('$1'... = the groups of the Pattern). To remove the match: \"\".")]
        [JsonProperty(Order = 2, Required = Required.Always)]
        public string Replacement { get; set; }

        private Regex _regex;
        public string GetFinalValue(string oldValue)
        {
            _regex ??= new Regex(this.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
            return _regex.Replace(oldValue, this.Replacement);
        }
    }
}