using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Text;
using FunscriptToolbox.Core.Infra;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    public abstract class AIEngineRunner
    {
        private static readonly Regex rs_lookLikeItsJson = new Regex(@"\s*\{.*"".*\:.*\}\s*|\[\s*\]", RegexOptions.Singleline | RegexOptions.Compiled);

        public static string TryToFixReceivedJson(
            AIRequest request,
            string json,
            bool tryToFixEnd = true)
        {
            var jsonOriginal = json;

            // Remove thinking text
            json = Regex.Replace(json, @"\<think\>.*\<\/think\>", string.Empty, RegexOptions.Multiline);
            json = Regex.Replace(json, @"^\s*>.*$", string.Empty, RegexOptions.Multiline);

            var indexOfFirstBracket = json.IndexOf('[');
            if (indexOfFirstBracket >= 0)
            {
                json = json.Substring(indexOfFirstBracket);
            }

            // Try to find and remove partial json object
            var bracesCounter = 0;
            var lastClosingBraceIndex = -1;
            var index = 0;

            // List to track indices of quotes that need escaping
            var unescapedQuoteIndices = new List<int>();

            while (index < json.Length)
            {
                // Skip string so that { or } inside the string don't mess up bracesCounter
                if (json[index] == '"')
                {
                    index++; // Move past the opening quote
                    while (index < json.Length)
                    {
                        // Case 1: Already escaped quote \"
                        if (index + 1 < json.Length && json[index] == '\\' && json[index + 1] == '"')
                        {
                            index += 2;
                        }
                        // Case 2: A quote " that might be the end or might need escaping
                        else if (json[index] == '"')
                        {
                            // LOOKAHEAD VALIDATION
                            // Check if this quote is followed by <space> and then [, } ] :
                            bool isRealEnding = false;
                            int k = index + 1;

                            // Skip upcoming whitespace to find the next significant character
                            while (k < json.Length && char.IsWhiteSpace(json[k]))
                            {
                                k++;
                            }

                            if (k < json.Length)
                            {
                                char nextChar = json[k];
                                // If the next char is a delimiter, this is likely the real end of the string
                                if (nextChar == ',' || nextChar == '}' || nextChar == ']' || nextChar == ':')
                                {
                                    isRealEnding = true;
                                }
                            }
                            else
                            {
                                // End of text reached; assume this is the closing quote
                                isRealEnding = true;
                            }

                            if (isRealEnding)
                            {
                                index++; // Skip closing "
                                break;   // Exit the string loop
                            }
                            else
                            {
                                // It's an unescaped quote inside the string. 
                                // Add to list and continue inside the string loop.
                                unescapedQuoteIndices.Add(index);
                                index++;
                            }
                        }
                        // Case 3: Normal character
                        else
                        {
                            index++;
                        }
                    }
                }
                else
                {
                    // Standard JSON structure parsing
                    var c = json[index++];
                    var diff = (c == '{')
                        ? 1
                        : (c == '}')
                        ? -1
                        : 0;
                    bracesCounter += diff;
                    if (diff != 0 && bracesCounter == 0)
                    {
                        lastClosingBraceIndex = index;
                    }
                }
            }

            // FIX 1: Fix unescaped quotes
            // We traverse in reverse order so that inserting characters doesn't mess up previous indices
            for (int i = unescapedQuoteIndices.Count - 1; i >= 0; i--)
            {
                int insertAt = unescapedQuoteIndices[i];
                json = json.Insert(insertAt, "\\");

                // IMPORTANT: Since we added a character, we must shift the lastClosingBraceIndex
                // if the insertion happened before it.
                if (lastClosingBraceIndex > insertAt)
                {
                    lastClosingBraceIndex++;
                }
            }

            // FIX 2: Fix incomplete JSON at the end
            if (tryToFixEnd && bracesCounter != 0 && lastClosingBraceIndex >= 0)
            {
                // Ensure the index is still within bounds (safety check)
                if (lastClosingBraceIndex <= json.Length)
                {
                    json = json.Substring(0, lastClosingBraceIndex);
                    json += "]";
                }
            }

            var indexOfLastBracket = json.LastIndexOf(']');
            if (tryToFixEnd && indexOfLastBracket < 0)
            {
                json += "]";
            }
            else
            {
                json = json.Substring(0, indexOfLastBracket + 1);
            }

            // Add ',' between fields
            json = Regex.Replace(json, @"(""(?:\\.|[^""])*"": ""(?:\\.|[^""])*""(?!\s*,))", "$1,");
            // Add ',' between braces
            json = Regex.Replace(json, @"(})(\s*{)", "$1,$2");

            if (!rs_lookLikeItsJson.IsMatch(json))
            {
                throw new AIResponseException(
                    request,
                    "The answer received from the AI doesn't look like JSON:",
                    jsonOriginal);
            }

            return json;
        }
    }
}