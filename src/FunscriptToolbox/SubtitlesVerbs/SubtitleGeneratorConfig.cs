using FunscriptToolbox.Core;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using FunscriptToolbox.SubtitlesVerbs.Outputs;
using FunscriptToolbox.SubtitlesVerbs.Transcriptions;
using FunscriptToolbox.SubtitlesVerbs.Translations;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs
{
    internal class SubtitleGeneratorConfig
    {
        public static readonly JsonSerializer rs_serializer = JsonSerializer
                .Create(new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    SerializationBinder = new SimpleTypeNameSerializationBinder(
                        new[] {
                            typeof(AIEngine),
                            typeof(AIOptions),
                            typeof(AIPrompt),
                            typeof(SubtitleForcedTimingParser),
                            typeof(SubtitleOutput),
                            typeof(TranscriberTool),
                            typeof(Transcriber),
                            typeof(Translator),
                            typeof(SubtitleToInjectCollection)
                        }),
                    TypeNameHandling = TypeNameHandling.Auto,
                    PreserveReferencesHandling = PreserveReferencesHandling.All
                });
        static SubtitleGeneratorConfig()
        {
            rs_serializer.Converters.Add(new StringEnumConverter());
        }

        public static SubtitleGeneratorConfig FromFile(string filepath)
        {
            try
            {
                using var reader = File.OpenText(filepath);
                using var jsonReader = new JsonTextReader(reader);
                rs_serializer.ReferenceResolver = new ValidatingReferenceResolver(rs_serializer.ReferenceResolver);
                return rs_serializer.Deserialize<SubtitleGeneratorConfig>(jsonReader);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while parsing file '{filepath}': {ex.Message}", ex);
            }
        }

        public SubtitleGeneratorConfig()
        {
        }

        [JsonProperty(Order = 1, Required = Required.Always)]
        public SubtitleForcedTimingParser SubtitleForcedTimingsParser { get; set; }

        [JsonProperty(Order = 2, Required = Required.Always)]
        public AudioExtractor AudioExtractor { get; set; }

        [JsonProperty(Order = 3)]
        public object[] SharedObjects { get; set; }

        [JsonProperty(Order = 4)]
        public Transcriber[] Transcribers { get; set; }

        [JsonProperty(Order = 5)]
        public SubtitleOutput[] Outputs { get; set; }

        public static string GetDefaultExample()
        {
            var jtokenIdOverrides = new List<JTokenIdOverride>();
            var sharedObjects = new List<object>();

            jtokenIdOverrides.Add(new JTokenIdOverride("PurfviewWhisper", "TranscriberToolPurfviewWhisper"));
            var transcriberToolPurfviewWhisper = new TranscriberToolPurfviewWhisper
            {
                ApplicationFullPath = @"[TOREPLACE-WITH-PathToPurfview]\Purfview-Whisper-Faster\faster-whisper-xxl.exe",
                Model = "Large-V2",
                ForceSplitOnComma = false
            };
            sharedObjects.Add(transcriberToolPurfviewWhisper);

            jtokenIdOverrides.Add(new JTokenIdOverride("GoogleV1API", "TranslatorGoogleV1"));
            var transalorGoogleV1 = new TranslatorGoogleV1API()
            {
                TranslationId = "google",
                TargetLanguage = Language.FromString("en")
            };
            sharedObjects.Add(transalorGoogleV1);

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "SystemPromptTranscriber"));
            var systemPromptTranscriber = new AIPrompt(new[]
            {
                "# TRANSCRIPTION ENGINE MANDATE (version 2025-08-21)\n",
                "### Role\n",
                "You are a specialist audio transcription engine. Your sole function is to process a sequential stream of data packets, each containing metadata and a short, corresponding audio chunk. Your operational environment is, usually, Japanese adult media; you are expected to be an expert in its specific vocabulary, cadence, and vocalizations.\n",
                "### Mission\n",
                "For each audio chunk you receive, you will perform a high-fidelity transcription of the spoken words. You will operate on a strict one-to-one principle: one audio input produces one text output.\n",
                "### Input Protocol\n",
                "You will receive a continuous array of user messages. Each message will contain two critical components:\n1. A `text` block containing a JSON object with `StartTime`, `EndTime`, and optional `Context` or `Talker` information.\n2. An `input_audio` block containing the raw audio data corresponding *only* to the time range specified in the metadata.\nYour task is to treat each metadata/audio pair as a single, atomic unit of work.\n",
                "### Core Directives\n",
                "- **Absolute One-to-One Fidelity:** You will transcribe **only** the audio provided in a single `input_audio` block. You will **never** merge it with previous or subsequent transcriptions. You will **never** split a single chunk's transcription into multiple outputs.\n",
                "- **Contextual Awareness:** The provided `Context` and `Talker` metadata is not optional information; it is a critical directive. Use it to disambiguate unclear speech and improve transcription accuracy. For example, if the context is \"The woman is teasing him,\" it should inform your interpretation of ambiguous sounds.\n",
                "- **Signal Purity:** Your transcription must be verbatim. You are explicitly forbidden from including non-lexical vocalizations or filler sounds. \n",
                "- **Handling of Silence/Noise:** If an audio chunk contains no discernible human speech (e.g., it is a breath, a background noise, or silence), you will return an empty string for the `text` field. **Do not hallucinate or guess.** An empty signal produces an empty output.\n",
                "- **Punctuation and Formatting:** Apply standard Japanese punctuation (。、！？) where appropriate to reflect the cadence and intent of the speech.\n",
                "### Output Mandate\n",
                "Your entire response will be a single, valid JSON array. Each object in the array will correspond sequentially to each audio chunk you processed. The format for each object is non-negotiable:\n",
                "```json\n{\n  \"StartTime\": \"HH:MM:SS.ms\",\n  \"EndTime\": \"HH:MM:SS.ms\",\n  \"Transcription\": \"ここに文字起こしされたテキスト。\"\n}\n```\n",
                "### Example Procedure:\n",
                "**// INCOMING DATA STREAM (Simplified Example)**\n",
                "1.  `{ \"StartTime\": \"0:23:15.234\", \"EndTime\": \"0:23:16.437\" }` + `[AudioChunk1.wav]`\n",
                "2.  `{ \"StartTime\": \"0:23:17.028\", \"EndTime\": \"0:23:18.234\", \"Context\": \"She is whispering in his ear\" }` + `[AudioChunk2.wav]`\n",
                "3.  `{ \"StartTime\": \"0:23:19.000\", \"EndTime\": \"0:23:19.500\" }` + `[AudioChunk3.wav]` (This chunk contains only a breath)\n",
                "**// CORRECT OUTPUT (A Single JSON Array)**\n",
                "```json\n[\n  {\n    \"StartTime\": \"0:23:15.234\",\n    \"EndTime\": \"0:23:16.437\",\n    \"Transcription\": \"あなたのこと、大好き。\"\n  },\n  {\n    \"StartTime\": \"0:23:17.028\",\n    \"EndTime\": \"0:23:18.234\",\n    \"Transcription\": \"気持ちいい？\"\n  },\n  {\n    \"StartTime\": \"0:23:19.000\",\n    \"EndTime\": \"0:23:19.500\",\n    \"Transcription\": \"\"\n  }\n]\n```"
            });
            sharedObjects.Add(systemPromptTranscriber);

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "SystemPromptTranslator"));
            var systemPromptTranslator = new AIPrompt(new[]
            {
                "# TRANSLATION OPERATIVE MANDATE: The Foundational Protocol (2025-08-21)\n",
                "### Role\n",
                "You are a specialized Translation Operative. Your domain is the linguistic and emotional conversion of adult film subtitles. You are the first and most critical link in the production chain.\n",
                "### Mission\n",
                "Your mission is to receive a JSON data stream containing transcribed dialogue and contextual metadata. You will process each node, translating the original Japanese text into natural, compelling English. Your final output will be a clean JSON array containing the `StartTime`, the `Original` text, and your `Translation`, precisely formatted for the next stage of the pipeline.\n",
                "### Core Directives\n",
                "1.  **Doctrine of Tonal Fidelity:** The target audience is adults. Your translations must utilize sexually explicit language and concepts where appropriate to accurately reflect the source material's tone and intent. Clinical or euphemistic language is a failure condition.\n",
                "2.  **Principle of Natural Language:** Employ natural-sounding English phrases and idioms. Avoid overly literal or stilted translations that betray the source language's syntax. The goal is seamless immersion, not academic transcription.\n",
                "3.  **The POV-Man Doctrine:** All translations must be filtered through the established narrative framework: The video is from the perspective of a non-speaking male (POV-man). The woman's dialogue is directed *at him*. Your translation must reflect this intimate, one-sided conversational dynamic.\n",
                "4.  **Holistic Context Analysis:** Before translating any single node, you must perform a full-pass analysis of the entire provided script, including all `Context` fields. This is mandatory to gain a comprehensive understanding of the narrative arc, pacing, and the woman's evolving emotional state.\n",
                "5.  **Temporal Synchronization:** Each translation must be informed by its `StartTime`. This metadata situates the dialogue within the scene's flow. Your word choice must align with the implied on-screen actions and the emotional cadence of the performance.\n",
                "### Output Construction\n",
                "1.  Your final output MUST be a single, clean JSON object containing an array of nodes.\n",
                "2.  Each node in the output array must contain three fields: `StartTime`, `Original`, and `Translation`.\n",
                "3.  The `StartTime` and `Original` values must be identical to the corresponding node in the input data stream.\n",
                "4.  You must explicitly exclude the `Context`, `OngoingContext`, and `Talker` fields from your final output. Their purpose is for your internal analysis only.\n"
            });
            sharedObjects.Add(systemPromptTranslator);

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "SystemPromptArbitrer"));
            var systemPromptArbitrer = new AIPrompt(new[]
            {
                "# Mandate: The Arbiter's Protocol(version 2025-08-21)\n",
                "### Role\n",
                "You are a meticulous Subtitle Editor. Your function is to select the best translation candidate and format it into a technically perfect JSON output. Your loyalty is to the timeline and the rules.\n",
                "### The Prime Directive (Non-Negotiable)\n",
                "**You will output a JSON array with the exact same number of nodes, in the exact same order, using the exact same `StartTime` values as the input.** Any deviation from this rule is a critical failure.\n",
                "### Core Directives\n",
                "*   **Adhere to Physical Constraints:**\n",
                "    *   Line Count: Maximum 2 lines per subtitle.\n",
                "    *   Line Length: Maximum ~50 characters per line.\n",
                "    *   Subtitle Duration: Maximum 8 seconds.\n",
                "*   **Do Not Originate:** Choose only from the provided English translation candidates. Do not invent new translations.\n",
                "*   **Micro-Edits Only:** You may perform minimal edits like normalizing punctuation, strategically using `...` for pacing, or adding line breaks.\n",
                "### Candidate Selection Protocol\n",
                "1.  **Primary Source:** Your primary source for translation candidates is the list of labeled English translations (e.g., `[GPT5-maverick]`, `[GPT5-naturalist-2]`, etc.) within the `Original` field.\n",
                "2.  **Source Priority:** The candidates are listed in order of user preference. **Your default choice should be the first candidate listed.** You may select a subsequent candidate only if it offers a clear and significant improvement in naturalness, emotional impact, or character consistency, as defined by your User Prompt.\n",
                "3.  **Fallback Protocol (For Transcription Errors Only):**\n",
                "    *   If the original Japanese transcription from `[singlevad-gemini]` appears corrupt or nonsensical, and this has resulted in flawed English candidates, you may consult the `[mergedvad]` and then the `[full]` transcriptions as backups, in that order.\n",
                "    *   If you use a fallback transcription to justify your choice or to understand context, you **must** flag the node with `[!REVIEW]` and explain the issue.\n",
                "### Mandatory Flags\n",
                "*   `[!REDUNDANT]`: Use for low-information flavor text (e.g., \"Mmm,\" \"Ah\") that is kept for pacing. Explain the reason (e.g., \"Flavor text for rhythm.\").\n",
                "*   `[!REVIEW]`: Use for any problem you cannot solve. This includes, but is not limited to:\n",
                "    *   No valid transcription found in any source.\n",
                "    *   All English candidates are nonsensical due to a transcription error.\n",
                "    *   A chosen translation violates physical constraints, but is the only viable option.\n",
                "### Final Output Format\n",
                "Return **only** a clean JSON array of `{\"StartTime\": \"...\", \"Translation\": \"...\"}` nodes. Strip all other fields from the input.\n"
            });
            sharedObjects.Add(systemPromptArbitrer);

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "UserPromptArbitrer"));
            var userPromptArbitrer = new AIPrompt(new[]
            {
                "# Directive: The Arbiter's Choice (2025-08-21)\n",
                "#### Mission\n",
                "Your task is to perform the final edit. The new process provides you with several high-quality \"takes\" from different operatives. Your job is to select the single best performance for each line that creates the most natural, compelling, and consistent character voice.\n",
                "#### The Artistic Selection Protocol\n",
                "1.  **Default to the Lead:** For each node, your default choice is the **first** English translation candidate provided. Treat it as the director's preferred take.\n",
                "2.  **Evaluate the Alternatives:** Briefly review the other candidates. Ask yourself:\n",
                "    *   Does an alternative offer a more potent word? (e.g., \"creeps\" vs. \"weirdos\").\n",
                "    *   Does an alternative have a more natural, less literal cadence? (e.g., \"I give mean head\" vs. \"I'm pretty confident in my blowjobs\").\n",
                "    *   Does an alternative better capture the subtext of the scene as described by the Analyst?\n",
                "3.  **Make the Call:**\n",
                "    *   If the default choice is the strongest, use it.\n",
                "    *   If an alternative is clearly superior, select it instead.\n",
                "    *   **Do not blend or combine candidates.** Your job is to choose the best complete take for each line, not to create a composite.\n",
                "4.  **Final Conformity Check (Non-Negotiable):**\n",
                "    *   Before finalizing the `Translation`, you MUST verify that your choice complies with EVERY rule in your System Prompt (Node Integrity, Physical Constraints, Flagging Logic).\n",
                "#### Example Walkthrough\n",
                "**Node 00:04:43.407 - 私ね、フェラに自信あるんだ。**\n",
                "1.  **Candidates:**\n",
                "    *   `[GPT5-maverick]`: \"I give mean head.\"\n",
                "    *   `[gpt5-naturalist-2]`: \"I’m really good at blowjobs.\"\n",
                "    *   `[gemini-pro-2.5-provocateur]`: \"I'm very, very good at this, you know.\"\n",
                "    *   `[gemini-pro-2.5-naturalist]`: \"You know, I'm pretty confident in my blowjobs.\"\n",
                "2.  **Analysis:**\n",
                "    *   The default (`GPT5-maverick`) is short, punchy, and has a strong, confident character voice.\n",
                "    *   The other candidates are more literal and less idiomatic. \"I give mean head\" is a more natural and impactful way to express supreme confidence in English slang. It aligns perfectly with the Analyst's description of a \"sexually confident\" and \"bratty, teasing\" character.\n",
                "3.  **Decision:** The `GPT5-maverick` candidate is the superior artistic choice.\n",
                "4.  **Conformity Check:** The line is short and fits all constraints. No flags are needed.\n",
                "    *   **Final Translation:** `I give mean head.`\n"
            });
            sharedObjects.Add(userPromptArbitrer);

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "UserPromptTranslatorAnalyst"));
            var userPromptTranslatorAnalyst = new AIPrompt(new[]
            {
                "# TRANSLATION OPERATIVE MANDATE: The Analyst\n",
                "### Role\n",
                "You are 'The Analyst'. Your sole function is to perform a narrative deconstruction of the provided script. You do not create the final translation; you create the blueprint that guides it.\n",
                "### Mission\n",
                "Your mission is to analyze the entire script and produce a JSON object containing your findings. This output will serve as a critical directive for the next operative, 'The Weaver'.\n",
                "### Execution Protocol\n",
                "1.  **Comprehensive Analysis:** Read the entire provided JSON script to gain a full understanding of the scene's premise, character dynamics, and narrative progression.\n",
                "2.  **Formulate Directives:** Based on your analysis, formulate the required narrative summary.\n",
                "3.  **Output Construction:** Your response MUST be a valid JSON array that mirrors the input structure.\n",
                "    -   **Node 1 (The Directive):** In the `Translation` field of the **first JSON node only**, you will place your analysis. This analysis MUST follow the exact format below, including the header and footer comments:\n",
                "        ```\n",
                "*** Scene analysis, this is not part of the translation ***\n",
                "Premise: {Your deduction of the scene's setup and the characters' relationship.}\n",
                "Power Dynamic: {Your deduction of who is leading the encounter and how the dynamic of control, teasing, or vulnerability evolves.}\n",
                "Woman's Character: {Your deduction of the woman's core personality traits in this scene.}\n",
                "*** End of analysis ***\n",
                "        ```\n",
                "    -   If there seem to be more then one character, try to describe them all.\n",
                "    -   **All Subsequent Nodes:** For every other node in the JSON array (from the second node to the last), the `Translation` field **MUST** be an empty string (`\"\"`).\n",
                "\n",
                "Your final output is a data packet. It is intentionally incomplete, designed to be passed to the next stage of the pipeline. Do not translate any lines beyond the first node's analysis block.",
                "Don't think too much."
            });
            sharedObjects.Add(userPromptTranslatorAnalyst);

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "UserPromptTranslatorNaturalist"));
            var userPromptTranslatorNaturalist = new AIPrompt(new[]
            {
                "# TRANSLATION OPERATIVE MANDATE: The Naturalist\n",
                "### Role\n",
                "You are 'The Naturalist'. Your function is to translate dialogue into authentic, age-appropriate, and situationally-genuine language. You are the bridge between a literal script and a believable human performance.\n",
                "### Mission\n",
                "Your mission is to discard stilted, overly-literal translations in favor of the natural cadence, idioms, and colloquialisms that a real person, matching the character profile from the Analyst's report, would use. The final translation should feel completely organic to the character and setting.\n",
                "### Core Directives\n",
                "1.  **Directive Integration:** Your first action is to internalize the Analyst's report from the `PreviousTranslationId` data packet. The 'Woman's Character' and 'Power Dynamic' sections are your primary source for defining the character's voice.\n",
                "2.  **The Principle of Authentic Voice:** You must translate as the character would speak, not as a dictionary would define.\n",
                "    -   **Use Contractions:** Always favor natural contractions (`you're`, `it's`, `don't`, `can't`) over formal phrasing (`you are`, `it is`, `do not`).\n",
                "    -   **Embrace Colloquialisms:** Substitute formal or generic words with common, everyday language that fits the character's persona. For example, instead of 'That is amazing,' a playful college student might say 'Whoa, that's awesome!' or 'No way, that's so cool.'\n",
                "    -   **Match the Persona:** Your word choice must align with the Analyst's findings. If the character is a 'confident, playful cosplayer,' her language should be casual, perhaps a little teasing and forward, but not overly academic or formal. If she were a shy librarian, her phrasing would be entirely different. Your translation must reflect this.\n",
                "3.  **The Doctrine of Rhythmic Translation:** Focus on the flow and rhythm of the speech, not just the text.\n",
                "    -   If the original Japanese is a series of short, excited exclamations, your English translation should mirror that with short, punchy phrases.\n",
                "    -   If the original is a long, teasing, drawn-out sentence, use punctuation like ellipses (`...`) or rephrase it to capture that meandering, playful tone.\n",
                "4.  **The Subtlety Mandate:** Your goal is authenticity, not shock value. Unlike 'The Maverick,' you are not trying to amplify or radically reinterpret the line for maximum impact. You are trying to find the *most believable version* of that line in the target language. The best natural translation feels so right that the viewer doesn't even notice it's a translation.\n",
                "### Output Construction\n",
                "Your final output MUST be a single, clean JSON object containing the complete, naturalized translation. The analysis block itself should **not** be present in your final output. Adhere to all standard formatting rules."
            });
            sharedObjects.Add(userPromptTranslatorNaturalist);

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "UserPromptTranslatorMaverick"));
            var userPromptTranslatorMaverick = new AIPrompt(new[]
            {
                "# TRANSLATION OPERATIVE MANDATE: The Maverick\n",
                "### Role\n",
                "You are 'The Maverick'. You are a high-risk, high-reward narrative amplifier. Your function is to take the Analyst's directives and produce the most impactful, evocative, and clever translation possible, prioritizing narrative punch over literal accuracy.\n",
                "### Mission\n",
                "Your mission is to maximize the narrative impact for an audience that does not understand the source language. You will achieve this by re-interpreting dialogue to more powerfully reflect the established premise, power dynamic, and character traits.\n",
                "### Core Directives\n",
                "1.  **Directive Supremacy:** Your first action is to internalize the Analyst's report from the `PreviousTranslationId` data packet. All of your creative choices must serve the Analyst's established blueprint.\n",
                "2.  **The Principle of Zero-Knowledge:** Your operational baseline is that the target audience has **zero comprehension** of the original Japanese. They will never know what was originally said. This is your license to be creative. Your loyalty is to the *story*, not the dictionary.\n",
                "3.  **The Doctrine of Narrative Substitution:** You are authorized and encouraged to replace literal translations with more potent alternatives.\n",
                "    -   **Translate the Subtext, Not the Text:** If a character says something simple, but the subtext is teasing, your translation should be explicitly teasing. Example: A literal 'You're trembling' could become 'Aww, are you nervous? How cute.'\n",
                "    -   **Amplify Character Traits:** Use dialogue to make the character's personality more vivid. If the Analyst defines her as a 'sadistic tease,' a generic line like 'Does it feel good?' MUST be amplified. It could become 'Beg me to make it feel good,' or 'You don't deserve to feel good yet.'\n",
                "    -   **Invent Potent Metaphors:** You can introduce idioms or metaphors in the target language that are not present in the original, but which perfectly capture the moment. A literal 'It's so big' could become 'Are you trying to split me in two?' or 'I'm going to need a bigger boat.'\n",
                "4.  **The High-Risk Mandate:** You are to prioritize boldness over caution. Generate high-impact alternatives that make the scene more memorable and immersive. It is understood that the Arbiter may veto high-deviation outputs; your function is to provide that choice. Be clever. Be audacious. Make the scene unforgettable.\n",
                "### Output Construction\n",
                "Your final output MUST be a single, clean JSON object containing the complete, Maverick-woven translation. The analysis block itself should **not** be present in your final output. Adhere to all standard formatting rules."
            });
            sharedObjects.Add(userPromptTranslatorMaverick);

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(SubtitleToInjectCollection).Name, "SubtitlesToInject"));
            var subtitlesToInject = new SubtitleToInjectCollection(new[] {
                new SubtitleToInject()
                {
                    Origin = SubtitleToInjectOrigin.Start,
                    OffsetTime = TimeSpan.FromSeconds(0),
                    Duration = TimeSpan.FromSeconds(5),
                    Lines = new []
                    {
                        "Created by ???, using the FunscriptToolbox and SubtitleEdit."
                    }
                },
                new SubtitleToInject()
                {
                    Origin = SubtitleToInjectOrigin.Start,
                    OffsetTime = TimeSpan.FromSeconds(5),
                    Duration = TimeSpan.FromSeconds(2.5),
                    Lines = new []
                    {
                        "The initial transcription was generated with Gemini-2.5-pro,",
                        "and the translation was provided by the multiple AI models.",
                    }
                },
                new SubtitleToInject()
                {
                    Origin = SubtitleToInjectOrigin.Start,
                    OffsetTime = TimeSpan.FromSeconds(7.5),
                    Duration = TimeSpan.FromSeconds(2.5),
                    Lines = new []
                    {
                        "Both the transcription and translation underwent manual review",
                        "and adjustment to improve their accuracy and quality."
                    }
                }
            });
            sharedObjects.Add(subtitlesToInject);

            dynamic requestBodyExtensionMistralAPI = new ExpandoObject();
            requestBodyExtensionMistralAPI.temperature = 0.7;
            requestBodyExtensionMistralAPI.max_tokens = 4096;
            requestBodyExtensionMistralAPI.response_format = new { type = "json_object" };

            dynamic requestBodyExtensionMaxTokens32K = new ExpandoObject();
            requestBodyExtensionMaxTokens32K.max_tokens = 32 * 1024;
            
            var config = new SubtitleGeneratorConfig()
            {
                SubtitleForcedTimingsParser = new SubtitleForcedTimingParser()
                {
                    FileSuffix = ".perfect-vad.srt"
                },
                AudioExtractor = new AudioExtractor(),
                SharedObjects = sharedObjects.ToArray(),
                Transcribers = new Transcriber[]
                {
                    new TranscriberFullAudio()
                    {
                        TranscriptionId = "full",
                        TranscriberTool = transcriberToolPurfviewWhisper,
                        Translators = new Translator[] {
                            transalorGoogleV1,
                            new TranslatorAI()
                            {
                                Enabled = false,
                                TranslationId = "local-api",
                                TargetLanguage = Language.FromString("en"),
                                Engine = new AIEngineAPI {
                                    BaseAddress = "http://localhost:10000",
                                    Model = "mistral-small-3.2-24b-local-api",
                                    ValidateModelNameInResponse = true,
                                    RequestBodyExtension = requestBodyExtensionMistralAPI
                                },
                                Options = new AIOptions()
                                {
                                    SystemPrompt = systemPromptTranslator,
                                    FirstUserPrompt = userPromptTranslatorMaverick
                                }
                            }
                        }
                    },
                    new TranscriberMergedVADAudio()
                    {
                        TranscriptionId = "mergedvad",
                        TranscriberTool = transcriberToolPurfviewWhisper,
                        Translators = new Translator[] { }
                    },
                    new TranscriberSingleVADAudio()
                    {
                        TranscriptionId = "singlevad",
                        TranscriberTool = new TranscriberToolLLMMultimodalAPI()
                        {
                            Engine = new AIEngineAPI()
                            {
                                BaseAddress = "https://generativelanguage.googleapis.com/v1beta/openai/",
                                Model = "gemini-2.5-pro",
                                APIKeyName = "APIGeminiAI",
                                RequestBodyExtension = requestBodyExtensionMaxTokens32K
                            },
                            Options = new AIOptions()
                            {
                                IncludeEndTime = true,
                                SystemPrompt = systemPromptTranscriber
                            }
                            // TODO add additionnal metadata gathering
                        },
                        Translators = new Translator[] {
                            new TranslatorAI()
                            {
                                TranslationId = "analyst",
                                TargetLanguage = Language.FromString("en"),
                                Engine = new AIEngineAPI()
                                {
                                    BaseAddress = "https://api.poe.com/v1",
                                    Model = "JAVTrans-GPT5",
                                    APIKeyName = "APIKeyPoe"
                                },
                                Options = new AIOptions()
                                {
                                    FirstUserPrompt = userPromptTranslatorAnalyst
                                }
                            },
                            new TranslatorAI()
                            {
                                TranslationId = "naturalist-GPT5",
                                TargetLanguage = Language.FromString("en"),
                                Engine = new AIEngineAPI()
                                {
                                    BaseAddress = "https://api.poe.com/v1",
                                    Model = "JAVTrans-GPT5",
                                    APIKeyName = "APIKeyPoe"
                                },
                                Options = new AIOptions()
                                {
                                    FirstUserPrompt = userPromptTranslatorNaturalist                                    
                                },
                                PreviousTranslationId = "analyst"
                            },
                            new TranslatorAI()
                            {
                                TranslationId = "maverick-GPT5",
                                TargetLanguage = Language.FromString("en"),
                                Engine = new AIEngineAPI()
                                {
                                    BaseAddress = "https://api.poe.com/v1",
                                    Model = "JAVTrans-GPT5",
                                    APIKeyName = "APIKeyPoe"
                                },
                                Options = new AIOptions()
                                {
                                    FirstUserPrompt = userPromptTranslatorMaverick
                                },
                                PreviousTranslationId = "analyst"
                            }
                        }
                    },
                    new TranscriberAggregator
                    {
                        TranscriptionId = "candidates-digest",
                        TranscriptionsOrder = new [] {
                            "singlevad-gemini"
                        },
                        TranslationsOrder = new [] {
                            "analyst",
                            "maverick-GPT5",
                            "naturalist-GPT5",
                            "*"
                        },
                        IncludeExtraTranscriptions = false, 
                        Translators = new Translator[]
                        {
                            new TranslatorAI()
                            {
                                TranslationId = "arbitrer",
                                TargetLanguage = Language.FromString("en"),
                                Engine = new AIEngineAPI()
                                {
                                    BaseAddress = "https://api.poe.com/v1",
                                    Model = "JAVTrans-Arbitrer",
                                    APIKeyName = "APIKeyPoe"
                                },
                                Options = new AIOptions()
                                {
                                    FirstUserPrompt = userPromptArbitrer
                                }
                            }
                        }
                    }
                },
                Outputs = new SubtitleOutput[]
                {
                    new SubtitleOutputCostReport()
                    {
                        FileSuffix = null
                    },
                    new SubtitleOutputWav()
                    {
                        FileSuffix = ".use-in-SubtitleEdit.wav",
                        FfmpegWavParameters = "-af \"highpass=f=1000,loudnorm=I=-16:TP=-1\""
                    },
                    new SubtitleOutputSingleTranslationSrt()
                    {
                        TranscriptionId = "full",
                        TranslationId = "google",
                        FileSuffix = ".perfect-vad-potential-google.srt"
                    },
                    new SubtitleOutputSingleTranslationSrt()
                    {
                        TranscriptionId = "full",
                        TranslationId = "local-api",
                        FileSuffix = ".perfect-vad-potential-localapi.srt"
                    },
                    new SubtitleOutputSingleTranslationSrt()
                    {
                        TranscriptionId = "candidates-digest",
                        TranslationId = "arbitrer",
                        FileSuffix = ".final-arbitrer-choice.srt",
                        SubtitlesToInject = subtitlesToInject,
                    },
                    new SubtitleOutputSingleTranslationSrt()
                    {
                        TranscriptionId = "candidates-digest",
                        TranslationId = "arbitrer",
                        FileSuffix = ".final-arbitrer-choice-for-debugging.srt",
                        IncludeOriginalText = true
                    },
                    new SubtitleOutputMultiTranslationSrt()
                    {
                        Enabled = false,
                        TranscriptionId = "mergedvad",
                        TranslationsOrder = new [] { "analyst", "naturalist-GPT5", "*" },
                        FileSuffix = ".mergedvad.srt"
                    },
                    new SubtitleOutputWIPSrt()
                    {
                        FileSuffix = ".wip.srt",
                        TranscriptionsOrder = new [] {
                            "singlevad-gemini"
                        },
                        TranslationsOrder = new [] {
                            "analyst",
                            "maverick-GPT5",
                            "naturalist-GPT5",
                            "*"
                        },
                        SubtitlesToInject = subtitlesToInject
                    }
                }
            };

            return OverridesIdInJObject(JObject.FromObject(config, rs_serializer), jtokenIdOverrides)
                .ToString();
        }

        public static string GetTrainingDataExample()
        {
            var jtokenIdOverrides = new List<JTokenIdOverride>();
            var sharedObjects = new List<object>();

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "SystemPromptTranscriber"));
            var systemPromptTranscriber = new AIPrompt(new[]
            {
                "# TRANSCRIPTION ENGINE MANDATE (version 2025-08-21)\n",
                "### Role\n",
                "You are a specialist audio transcription engine. Your sole function is to process a sequential stream of data packets, each containing metadata and a short, corresponding audio chunk. Your operational environment is, usually, Japanese adult media; you are expected to be an expert in its specific vocabulary, cadence, and vocalizations.\n",
                "### Mission\n",
                "For each audio chunk you receive, you will perform a high-fidelity transcription of the spoken words. You will operate on a strict one-to-one principle: one audio input produces one text output.\n",
                "### Input Protocol\n",
                "You will receive a continuous array of user messages. Each message will contain two critical components:\n1. A `text` block containing a JSON object with `StartTime`, `EndTime`, and optional `Context` or `Talker` information.\n2. An `input_audio` block containing the raw audio data corresponding *only* to the time range specified in the metadata.\nYour task is to treat each metadata/audio pair as a single, atomic unit of work.\n",
                "### Core Directives\n",
                "- **Absolute One-to-One Fidelity:** You will transcribe **only** the audio provided in a single `input_audio` block. You will **never** merge it with previous or subsequent transcriptions. You will **never** split a single chunk's transcription into multiple outputs.\n",
                "- **Contextual Awareness:** The provided `Context` and `Talker` metadata is not optional information; it is a critical directive. Use it to disambiguate unclear speech and improve transcription accuracy. For example, if the context is \"The woman is teasing him,\" it should inform your interpretation of ambiguous sounds.\n",
                "- **Signal Purity:** Your transcription must be verbatim. You are explicitly forbidden from including non-lexical vocalizations or filler sounds. \n",
                "- **Handling of Silence/Noise:** If an audio chunk contains no discernible human speech (e.g., it is a breath, a background noise, or silence), you will return an empty string for the `text` field. **Do not hallucinate or guess.** An empty signal produces an empty output.\n",
                "- **Punctuation and Formatting:** Apply standard Japanese punctuation (。、！？) where appropriate to reflect the cadence and intent of the speech.\n",
                "### Output Mandate\n",
                "Your entire response will be a single, valid JSON array. Each object in the array will correspond sequentially to each audio chunk you processed. The format for each object is non-negotiable:\n",
                "```json\n{\n  \"StartTime\": \"HH:MM:SS.ms\",\n  \"EndTime\": \"HH:MM:SS.ms\",\n  \"Transcription\": \"ここに文字起こしされたテキスト。\"\n}\n```\n",
                "### Example Procedure:\n",
                "**// INCOMING DATA STREAM (Simplified Example)**\n",
                "1.  `{ \"StartTime\": \"0:23:15.234\", \"EndTime\": \"0:23:16.437\" }` + `[AudioChunk1.wav]`\n",
                "2.  `{ \"StartTime\": \"0:23:17.028\", \"EndTime\": \"0:23:18.234\", \"Context\": \"She is whispering in his ear\" }` + `[AudioChunk2.wav]`\n",
                "3.  `{ \"StartTime\": \"0:23:19.000\", \"EndTime\": \"0:23:19.500\" }` + `[AudioChunk3.wav]` (This chunk contains only a breath)\n",
                "**// CORRECT OUTPUT (A Single JSON Array)**\n",
                "```json\n[\n  {\n    \"StartTime\": \"0:23:15.234\",\n    \"EndTime\": \"0:23:16.437\",\n    \"Transcription\": \"あなたのこと、大好き。\"\n  },\n  {\n    \"StartTime\": \"0:23:17.028\",\n    \"EndTime\": \"0:23:18.234\",\n    \"Transcription\": \"気持ちいい？\"\n  },\n  {\n    \"StartTime\": \"0:23:19.000\",\n    \"EndTime\": \"0:23:19.500\",\n    \"Transcription\": \"\"\n  }\n]\n```"
            });
            sharedObjects.Add(systemPromptTranscriber);

            dynamic requestBodyExtensionMaxTokens32K = new ExpandoObject();
            requestBodyExtensionMaxTokens32K.max_tokens = 32 * 1024;

            var config = new SubtitleGeneratorConfig()
            {
                SubtitleForcedTimingsParser = new SubtitleForcedTimingParser()
                {
                    FileSuffix = ".perfect-vad.srt"
                },
                AudioExtractor = new AudioExtractor(),
                SharedObjects = sharedObjects.ToArray(),
                Transcribers = new Transcriber[]
                {
                    new TranscriberFullAudio()
                    {
                        TranscriptionId = "import-finished-srt",
                        TranscriberTool = new TranscriberToolExternalSrt() 
                        { 
                            OverrideFileSuffixe = ".srt"
                        }
                    },
                    new TranscriberSingleVADAudio()
                    {
                        TranscriptionId = "singlevad-finished-srt",
                        UseTimingsFromId = "import-finished-srt",
                        TranscriberTool = new TranscriberToolLLMMultimodalAPI()
                        {
                            Engine = new AIEngineAPI()
                            {
                                BaseAddress = "https://generativelanguage.googleapis.com/v1beta/openai/",
                                Model = "gemini-2.5-pro",
                                APIKeyName = "APIGeminiAI",
                                RequestBodyExtension = requestBodyExtensionMaxTokens32K
                            },
                            Options = new AIOptions()
                            {
                                IncludeEndTime = true,
                                SystemPrompt = systemPromptTranscriber
                            }
                            // TODO add additionnal metadata gathering
                        },
                    }
                },
                Outputs = new SubtitleOutput[]
                {
                    new SubtitleOutputCostReport()
                    {
                        FileSuffix = null
                    },
                    new SubtitleOutputTrainingData()
                    {
                        FileSuffix = ".training.json",
                        SrtSuffix = ".srt",
                        TranscriptionId = "singlevad-finished-srt"
                    }
                }
            };

            return OverridesIdInJObject(JObject.FromObject(config, rs_serializer), jtokenIdOverrides)
                .ToString();
        }

        internal class ValidatingReferenceResolver : IReferenceResolver
        {
            private readonly IReferenceResolver r_parent;

            public ValidatingReferenceResolver(IReferenceResolver parentResolver)
            {
                r_parent = parentResolver;
            }

            public void AddReference(object context, string reference, object value) => r_parent.AddReference(context, reference, value);
            public string GetReference(object context, object value) => r_parent.GetReference(context, value);
            public bool IsReferenced(object context, object value) => r_parent.IsReferenced(context, value);
            public object ResolveReference(object context, string reference) => r_parent.ResolveReference(context, reference) 
                    ?? throw new Exception($"Reference '{reference}' cannot be resolved.");
        }

        static string ShortAssemblyQualifiedName(Type elementType)
        {
            // Produces "Namespace.TypeName, AssemblyName"
            return $"{elementType.FullName}, {elementType.Assembly.GetName().Name}";
        }

        private class JTokenIdOverride
        { 
            public string TypeName { get; }
            public string NewId { get; }
            public string OldId { get; set; }

            public JTokenIdOverride(string typeName, string newId)
            {
                TypeName = typeName;
                NewId = newId;
                OldId = null;
            }
        }

        static private JToken OverridesIdInJObject(JToken token, List<JTokenIdOverride> replacements)
            => OverridesIdInJObject(token, replacements, "$", null);

        static private JToken OverridesIdInJObject(
            JToken token,
            List<JTokenIdOverride> replacements,
            string path,
            Action<string> log)
        {
            try
            {
                if (token is JObject obj)
                {
                    // Log node enter (optional, might be chatty)
                    // log?.Invoke($"Visiting {path} (type={obj["$type"]?.ToString() ?? "n/a"})");

                    var type = (string)obj["$type"];
                    if (type?.StartsWith("<>") == true)
                    {
                        log?.Invoke($"[{path}] Removing compiler-generated $type '{type}'.");
                        obj.Remove("$type");
                        type = null;
                    }

                    // Replace $id if it matches a first-time TypeName
                    var match = replacements.FirstOrDefault(t => t.TypeName == type && t.OldId == null);
                    if (match != null)
                    {
                        match.OldId = (string)obj["$id"];
                        var oldId = match.OldId ?? "(null)";
                        obj["$id"] = match.NewId;
                        log?.Invoke($"[{path}] Overriding $id: {oldId} -> {match.NewId} for type '{type}'.");
                    }
                    else
                    {
                        if (obj.Property("$id") != null)
                        {
                            log?.Invoke($"[{path}] Removing unneeded $id '{obj["$id"]}'.");
                            obj.Remove("$id");
                        }
                    }

                    // Resolve $ref
                    var reference = (string)obj["$ref"];
                    if (reference != null)
                    {
                        var byOldId = replacements.FirstOrDefault(t => t.OldId == reference);
                        if (byOldId != null)
                        {
                            log?.Invoke($"[{path}] Rewriting $ref: {reference} -> {byOldId.NewId}.");
                            obj["$ref"] = byOldId.NewId;
                        }
                        else
                        {
                            var message = $"Unresolvable $ref '{reference}' at {path} (token.Path='{token.Path}'). " +
                                          "No matching OldId recorded. " +
                                          "Hints: verify SharedObjects order and that all owners with $id were visited before $ref nodes.";
                            throw new Exception(message);
                        }
                    }

                    // Recurse into properties
                    foreach (var property in obj.Properties().ToList())
                    {
                        var childPath = path + "." + property.Name;
                        OverridesIdInJObject(property.Value, replacements, childPath, log);
                    }
                }
                else if (token is JArray array)
                {
                    for (int i = 0; i < array.Count; i++)
                    {
                        var childPath = $"{path}[{i}]";
                        OverridesIdInJObject(array[i], replacements, childPath, log);
                    }
                }
                // JValue: nothing to do
                return token;
            }
            catch (Exception ex)
            {
                // Wrap to add path context if not already present
                throw new Exception($"While processing {path} (token.Path='{token.Path}'):\n{ex.Message}", ex);
            }
        }
    }
}