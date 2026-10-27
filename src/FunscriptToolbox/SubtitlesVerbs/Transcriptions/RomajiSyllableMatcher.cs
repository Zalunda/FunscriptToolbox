using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Transcriptions
{
    /// <summary>What the heard text says about one side (start or end) of a clip.</summary>
    public enum SideState
    {
        /// <summary>The first/last syllable of the original text was heard.</summary>
        Ok,
        /// <summary>Syllables of the original text are missing on that side (clip probably cuts speech).</summary>
        Missing,
        /// <summary>More was heard than the original text on that side (clip probably includes neighbouring speech).</summary>
        Extra,
        /// <summary>A few syllables were heard in place of the expected ones (speech is there, transcribed differently).</summary>
        Substituted
    }

    /// <summary>Result of comparing the original text with what the AI heard, per side.</summary>
    public class MatchAnalysis
    {
        public bool IsMismatch { get; set; }
        public double Similarity { get; set; }

        public SideState StartState { get; set; }
        public SideState EndState { get; set; }

        // Syllables of the original not heard before the first aligned one / after the last aligned one.
        public int MissingStart { get; set; }
        public int MissingEnd { get; set; }

        // Syllables heard before the first aligned one / after the last aligned one.
        public int ExtraStart { get; set; }
        public int ExtraEnd { get; set; }

        public SideState State(BoundarySide side) => side == BoundarySide.Start ? StartState : EndState;
        public int Missing(BoundarySide side) => side == BoundarySide.Start ? MissingStart : MissingEnd;
        public int Extra(BoundarySide side) => side == BoundarySide.Start ? ExtraStart : ExtraEnd;

        /// <summary>True when the clip does not cut speech on that side (Ok, Extra or Substituted).</summary>
        public bool Covers(BoundarySide side) => !IsMismatch && State(side) != SideState.Missing;

        [JsonIgnore]
        public bool CoversAll => Covers(BoundarySide.Start) && Covers(BoundarySide.End);

        public string ToTraceString()
        {
            if (IsMismatch)
                return $"Mismatch {Similarity:0.00}";
            return $"S={SideToString(StartState, MissingStart, ExtraStart)} E={SideToString(EndState, MissingEnd, ExtraEnd)} {Similarity:0.00}";
        }

        private static string SideToString(SideState state, int missing, int extra)
        {
            switch (state)
            {
                case SideState.Missing: return $"Miss{missing}";
                case SideState.Extra: return $"Ext{extra}";
                case SideState.Substituted: return $"Sub{missing}";
                default: return "Ok";
            }
        }
    }

    /// <summary>
    /// Romaji syllable (mora) extraction, fuzzy syllable similarity and alignment of an original
    /// text with what the AI heard.
    /// </summary>
    public static class RomajiSyllableMatcher
    {
        private struct SyllableAlignItem
        {
            public int CandIndex;
            public int HeardIndex;
            public double Similarity;
        }

        /// <summary>
        /// What does the heard text say about the clip?
        /// Uses sequence diff/alignment on syllables to find, for each side, whether syllables
        /// of the original are missing, extra syllables were heard, or both (substitution).
        /// </summary>
        /// <param name="maxSubstitutedSyllables">
        /// A side with N missing syllables and at least N extra syllables counts as Substituted
        /// (covered) only when N is at most this value; otherwise it stays Missing.
        /// </param>
        public static MatchAnalysis Analyze(string candidateText, string heardText, int maxSubstitutedSyllables = 2)
        {
            var result = new MatchAnalysis
            {
                Similarity = TextSimilarity(candidateText, heardText)
            };

            if (string.IsNullOrWhiteSpace(candidateText) || string.IsNullOrWhiteSpace(heardText))
                return AsMismatch(result);

            var cList = ExtractSyllables(candidateText);
            var hList = ExtractSyllables(heardText);

            if (cList.Count == 0 || hList.Count == 0)
                return AsMismatch(result);

            // Fast path: exact syllable sequence match
            if (cList.Count == hList.Count)
            {
                bool allSame = true;
                for (int i = 0; i < cList.Count; i++)
                {
                    if (cList[i] != hList[i])
                    {
                        allSame = false;
                        break;
                    }
                }
                if (allSame)
                {
                    result.StartState = SideState.Ok;
                    result.EndState = SideState.Ok;
                    return result;
                }
            }

            var align = Align(cList, hList);

            // Find matching syllables
            double matchScore = 0;
            int firstMatchIdx = -1;
            int lastMatchIdx = -1;

            for (int k = 0; k < align.Count; k++)
            {
                var item = align[k];
                if (item.CandIndex >= 0 && item.HeardIndex >= 0 && item.Similarity >= 0.35)
                {
                    matchScore += item.Similarity;
                    if (firstMatchIdx < 0)
                        firstMatchIdx = k;
                    lastMatchIdx = k;
                }
            }

            if (firstMatchIdx < 0)
                return AsMismatch(result);

            int n = cList.Count;
            int m = hList.Count;
            double candCoverage = matchScore / n;
            double heardCoverage = matchScore / m;

            // If neither sequence has sufficient correspondence, it's a mismatch
            if (candCoverage < 0.35 && heardCoverage < 0.35)
                return AsMismatch(result);

            result.MissingStart = align[firstMatchIdx].CandIndex;
            result.ExtraStart = align[firstMatchIdx].HeardIndex;
            result.MissingEnd = (n - 1) - align[lastMatchIdx].CandIndex;
            result.ExtraEnd = (m - 1) - align[lastMatchIdx].HeardIndex;

            result.StartState = ToSideState(result.MissingStart, result.ExtraStart, maxSubstitutedSyllables);
            result.EndState = ToSideState(result.MissingEnd, result.ExtraEnd, maxSubstitutedSyllables);
            return result;
        }

        private static MatchAnalysis AsMismatch(MatchAnalysis result)
        {
            result.IsMismatch = true;
            result.StartState = SideState.Missing;
            result.EndState = SideState.Missing;
            return result;
        }

        private static SideState ToSideState(int missing, int extra, int maxSubstitutedSyllables)
        {
            if (missing > 0)
            {
                // The confidence in a substitution drops quickly with the number of syllables involved
                return (extra >= missing && missing <= maxSubstitutedSyllables)
                    ? SideState.Substituted
                    : SideState.Missing;
            }
            return extra > 0 ? SideState.Extra : SideState.Ok;
        }

        /// <summary>DP diff alignment of two syllable sequences (candidate vs heard).</summary>
        private static List<SyllableAlignItem> Align(List<string> cList, List<string> hList)
        {
            int n = cList.Count;
            int m = hList.Count;

            // DP diff alignment matrix: d[i, j] = minimum edit cost to align cList[0..i-1] with hList[0..j-1]
            var d = new double[n + 1, m + 1];
            var dir = new byte[n + 1, m + 1]; // 1 = diag (aligned), 2 = up (cand deletion), 3 = left (heard insertion)

            for (int i = 0; i <= n; i++)
            {
                d[i, 0] = i * 1.0;
                dir[i, 0] = 2;
            }
            for (int j = 0; j <= m; j++)
            {
                d[0, j] = j * 1.0;
                dir[0, j] = 3;
            }
            dir[0, 0] = 0;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    double sim = SingleSyllableSimilarity(cList[i - 1], hList[j - 1]);
                    double subCost = (sim >= 0.25) ? 2.0 * (1.0 - sim) : 2.5;

                    double costDiag = d[i - 1, j - 1] + subCost;
                    double costUp = d[i - 1, j] + 1.0;   // cand deleted
                    double costLeft = d[i, j - 1] + 1.0; // heard inserted

                    if (costDiag <= costUp && costDiag <= costLeft)
                    {
                        d[i, j] = costDiag;
                        dir[i, j] = 1;
                    }
                    else if (costUp <= costLeft)
                    {
                        d[i, j] = costUp;
                        dir[i, j] = 2;
                    }
                    else
                    {
                        d[i, j] = costLeft;
                        dir[i, j] = 3;
                    }
                }
            }

            // Traceback from (n, m) to (0, 0)
            var align = new List<SyllableAlignItem>(n + m);
            int curI = n, curJ = m;
            while (curI > 0 || curJ > 0)
            {
                byte step = dir[curI, curJ];
                if (step == 1)
                {
                    double sim = SingleSyllableSimilarity(cList[curI - 1], hList[curJ - 1]);
                    align.Add(new SyllableAlignItem { CandIndex = curI - 1, HeardIndex = curJ - 1, Similarity = sim });
                    curI--;
                    curJ--;
                }
                else if (step == 2)
                {
                    align.Add(new SyllableAlignItem { CandIndex = curI - 1, HeardIndex = -1, Similarity = 0 });
                    curI--;
                }
                else
                {
                    align.Add(new SyllableAlignItem { CandIndex = -1, HeardIndex = curJ - 1, Similarity = 0 });
                    curJ--;
                }
            }
            align.Reverse();
            return align;
        }

        /// <summary>0 (nothing in common) .. 1 (identical), ignoring punctuation, spacing and case.</summary>
        public static double TextSimilarity(string a, string b)
        {
            return SyllableSequenceSimilarity(ExtractSyllables(a), ExtractSyllables(b));
        }

        /// <summary>
        /// Splits Romaji (hyphenated or unhyphenated) into normalized canonical morae/syllables.
        /// Examples: "Se-n-pa-i" / "Sen-pai" / "Senpai" -> ["se", "n", "pa", "i"]
        ///           "Ki-mo-chi-i" / "Ki-mo-chii"       -> ["ki", "mo", "chi", "i"]
        ///           "I-ppa-i" / "Ip-pa-i"              -> ["i", "pa", "i"]
        /// </summary>
        public static List<string> ExtractSyllables(string text)
        {
            var syllables = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return syllables;

            var tokenBuilder = new StringBuilder();
            foreach (char ch in text)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    tokenBuilder.Append(char.ToLowerInvariant(ch));
                }
                else if (tokenBuilder.Length > 0)
                {
                    AppendTokenMorae(tokenBuilder.ToString(), syllables);
                    tokenBuilder.Clear();
                }
            }
            if (tokenBuilder.Length > 0)
                AppendTokenMorae(tokenBuilder.ToString(), syllables);

            return syllables;
        }

        private static void AppendTokenMorae(string token, List<string> syllables)
        {
            // Normalize trailing breath 'h' after a vowel (e.g. "ah" -> "a", "oh" -> "o")
            if (token.Length == 2 && IsRomajiVowel(token[0]) && token[1] == 'h')
            {
                syllables.Add(token[0].ToString());
                return;
            }

            int i = 0;
            while (i < token.Length)
            {
                char c = token[i];

                if (char.IsDigit(c))
                {
                    int start = i;
                    while (i < token.Length && char.IsDigit(token[i])) i++;
                    syllables.Add(token.Substring(start, i - start));
                    continue;
                }

                // 1. Standalone vowel mora (a, i, u, e, o)
                if (IsRomajiVowel(c))
                {
                    syllables.Add(c.ToString());
                    i++;
                    continue;
                }

                // 2. Moraic 'n' (at end of token, or before a non-vowel and non-'y')
                if (c == 'n' && (i + 1 == token.Length || (!IsRomajiVowel(token[i + 1]) && token[i + 1] != 'y')))
                {
                    syllables.Add("n");
                    i++;
                    // Skip doubled 'nn' inside the same dash-token if followed by a consonant or end
                    if (i < token.Length && token[i] == 'n' && (i + 1 == token.Length || (!IsRomajiVowel(token[i + 1]) && token[i + 1] != 'y')))
                        i++;
                    continue;
                }

                // 3. Geminate double consonant (e.g. "ppa" -> "pa", "tte" -> "te", "tch" -> "ch")
                if (i + 1 < token.Length && c == token[i + 1] && c != 'n')
                {
                    i++;
                    continue;
                }
                if (c == 't' && i + 2 < token.Length && token[i + 1] == 'c' && token[i + 2] == 'h')
                {
                    i++;
                    continue;
                }

                // 4. Consonant cluster + vowel
                int onsetStart = i;
                while (i < token.Length && !IsRomajiVowel(token[i]) && !char.IsDigit(token[i]))
                {
                    i++;
                }

                if (i < token.Length && IsRomajiVowel(token[i]))
                {
                    string rawMora = token.Substring(onsetStart, i - onsetStart + 1);
                    i++;
                    // Skip trailing silent 'h' in interjections like "haa" / "ah" if written "hah"
                    if (i < token.Length && token[i] == 'h' && (i + 1 == token.Length || !IsRomajiVowel(token[i + 1])))
                        i++;
                    syllables.Add(NormalizeMora(rawMora));
                }
                else
                {
                    // Trailing consonant with no vowel (e.g. "ip" from "ip-pa-i"): ignore geminate tail
                    break;
                }
            }
        }

        private static bool IsRomajiVowel(char c)
        {
            return c == 'a' || c == 'i' || c == 'u' || c == 'e' || c == 'o';
        }

        private static string NormalizeMora(string mora)
        {
            switch (mora)
            {
                case "si": return "shi";
                case "ti": return "chi";
                case "tu": return "tsu";
                case "hu": return "fu";
                case "zi":
                case "di": return "ji";
                case "du": return "zu";
                case "sya": return "sha";
                case "syu": return "shu";
                case "syo": return "sho";
                case "tya": return "cha";
                case "tyu": return "chu";
                case "tyo": return "cho";
                case "zya": return "ja";
                case "zyu": return "ju";
                case "zyo": return "jo";
                default: return mora;
            }
        }

        private static double SyllableSequenceSimilarity(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count == 0 && b.Count == 0) return 1.0;
            if (a.Count == 0 || b.Count == 0) return 0.0;
            return 1.0 - SyllableLevenshtein(a, b) / Math.Max(a.Count, b.Count);
        }

        private static double SyllableLevenshtein(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            var prev = new double[b.Count + 1];
            var cur = new double[b.Count + 1];

            for (int j = 0; j <= b.Count; j++)
                prev[j] = j;

            for (int i = 1; i <= a.Count; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Count; j++)
                {
                    double subCost = 1.0 - SingleSyllableSimilarity(a[i - 1], b[j - 1]);
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1.0, prev[j] + 1.0), prev[j - 1] + subCost);
                }
                var tmp = prev; prev = cur; cur = tmp;
            }

            return prev[b.Count];
        }

        /// <summary>
        /// Graduated similarity between two individual syllables/morae (0.0 .. 1.0).
        /// Rewards identical vowels and phonetically close consonants when audio is slurred/breathless.
        /// </summary>
        private static double SingleSyllableSimilarity(string a, string b)
        {
            if (a == b) return 1.0;
            if (a.Length == 0 || b.Length == 0) return 0.0;

            char vowelA = a[a.Length - 1];
            char vowelB = b[b.Length - 1];
            bool hasVowelA = IsRomajiVowel(vowelA);
            bool hasVowelB = IsRomajiVowel(vowelB);

            if (!hasVowelA || !hasVowelB)
            {
                // Moraic 'n' vs nasal syllable ('na', 'ni', 'nu', 'ne', 'no', 'mu')
                if ((a == "n" && b[0] == 'n') || (b == "n" && a[0] == 'n') || (a == "n" && b == "mu") || (b == "n" && a == "mu"))
                    return 0.4;
                return 0.0;
            }

            string onsetA = a.Substring(0, a.Length - 1);
            string onsetB = b.Substring(0, b.Length - 1);

            if (vowelA == vowelB)
            {
                if (ArePhoneticallyCloseOnsets(onsetA, onsetB))
                    return 0.75;
                return 0.50;
            }

            if (onsetA.Length > 0 && onsetA == onsetB)
                return 0.40;

            return 0.0;
        }

        private static bool ArePhoneticallyCloseOnsets(string onsetA, string onsetB)
        {
            if (onsetA == onsetB) return true;

            // Breath/aspirate vs pure vowel ("ha" vs "a", "fu" vs "u")
            if ((onsetA.Length == 0 && (onsetB == "h" || onsetB == "f")) ||
                (onsetB.Length == 0 && (onsetA == "h" || onsetA == "f")))
                return true;

            return GetOnsetGroup(onsetA) == GetOnsetGroup(onsetB) && GetOnsetGroup(onsetA) != 0;
        }

        private static int GetOnsetGroup(string onset)
        {
            switch (onset)
            {
                case "k":
                case "g":
                case "ky":
                case "gy":
                    return 1; // Velars
                case "s":
                case "sh":
                case "z":
                case "j":
                case "ts":
                case "ch":
                    return 2; // Sibilants & affricates
                case "t":
                case "d":
                case "n":
                case "ny":
                case "r":
                case "ry":
                case "m":
                case "my":
                case "b":
                case "by":
                case "p":
                case "py":
                case "h":
                case "hy":
                case "f":
                case "w":
                    return 3; // Labials / coronals / liquids (commonly slurred in muffled speech, e.g. bi/ri)
                default:
                    return 0;
            }
        }
    }
}
