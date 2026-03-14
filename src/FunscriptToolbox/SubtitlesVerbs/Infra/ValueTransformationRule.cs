using System.Text.RegularExpressions;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public class ValueTransformationRule
    {
        public string Pattern { get; set; }
        public string Replacement { get; set; }

        private Regex _regex;
        public string GetFinalValue(string oldValue)
        {
            _regex ??= new Regex(this.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
            return _regex.Replace(oldValue, this.Replacement);
        }
    }
}