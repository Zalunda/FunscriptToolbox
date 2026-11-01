using System;
using System.Linq;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions.Refiner
{
    /// <summary>
    /// Language-agnostic comparison of two texts: no dictionary and no kanji/kana rules, only a normalisation
    /// that removes what never changes the words (width of the characters, hiragana/katakana, case, punctuation, spaces).
    /// </summary>
    public static class TextSimilarity
    {
        private const char HiraganaStart = 'ぁ';
        private const char HiraganaEnd = 'ゖ';
        private const char KatakanaStart = 'ァ';
        private const char KatakanaEnd = 'ヶ';
        private const int KatakanaToHiraganaOffset = KatakanaStart - HiraganaStart;

        /// <summary>Compatibility form (full/half width), katakana to hiragana, lower case, letters and digits only.</summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            var sb = new StringBuilder(text.Length);
            foreach (var ch in text.Normalize(NormalizationForm.FormKC))
            {
                if (!char.IsLetterOrDigit(ch))
                    continue;

                var c = ch >= KatakanaStart && ch <= KatakanaEnd ? (char)(ch - KatakanaToHiraganaOffset) : ch;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>1 = same once normalised, 0 = nothing in common (1 - edit distance / length of the longest).</summary>
        public static double Ratio(string a, string b)
        {
            var na = Normalize(a);
            var nb = Normalize(b);
            if (na.Length == 0 && nb.Length == 0)
                return 1.0;

            return 1.0 - (double)EditDistance(na, nb) / Math.Max(na.Length, nb.Length);
        }

        /// <summary>Levenshtein distance (the texts are one subtitle long, so the simple version is enough).</summary>
        private static int EditDistance(string a, string b)
        {
            var previous = Enumerable.Range(0, b.Length + 1).ToArray();
            var current = new int[b.Length + 1];

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                    current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
                }
                (previous, current) = (current, previous);
            }
            return previous[b.Length];
        }

        /// <summary>
        /// True when 'text' is a unit of at least 'minUnit' characters (and 3 different ones) repeated at least 'minRepeats' times,
        /// back to back, over almost all of the text. (Units made of one or two characters are left alone:
        /// laughs and moans such as "ああああ" or "あっあっあっ" repeat legitimately.)
        /// </summary>
        public static bool IsRepetitionLoop(string text, int minRepeats = 4, int minUnit = 3)
        {
            var n = Normalize(text);
            for (int unit = minUnit; unit * minRepeats <= n.Length; unit++)
            {
                if (n.Substring(0, unit).Distinct().Count() < 3)
                    continue;

                int matches = 0;
                for (int i = 0; i + unit < n.Length; i++)
                {
                    if (n[i] == n[i + unit])
                        matches++;
                }

                // 90% of the characters equal to the one a unit later: the text is the same unit over and over
                if (matches >= 0.9 * (n.Length - unit))
                    return true;
            }
            return false;
        }
    }
}
