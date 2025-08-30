using FunscriptToolbox.Core;
using FunscriptToolbox.Core.Infra;
using FunscriptToolbox.SubtitlesVerbs.AudioExtraction;
using FunscriptToolbox.SubtitlesVerbs.Infra;
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
    public class SubtitleGeneratorConfig
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
                            typeof(SubtitleOutput),
                            typeof(TranscriberToolAudio),
                            typeof(Transcriber),
                            typeof(Translator)
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
        public AudioExtractor AudioExtractor { get; set; }

        [JsonProperty(Order = 2)]
        public object[] SharedObjects { get; set; }

        [JsonProperty(Order = 3)]
        public SubtitleWorker[] Workers { get; set; }

        public static string GetDefaultExample()
        {
            var jtokenIdOverrides = new List<JTokenIdOverride>();
            var sharedObjects = new List<object>();

            jtokenIdOverrides.Add(new JTokenIdOverride("PurfviewWhisper", "TranscriberToolPurfviewWhisper"));
            var transcriberToolPurfviewWhisper = new TranscriberToolAudioPurfviewWhisper
            {
                ApplicationFullPath = @"[TOREPLACE-WITH-PathToPurfview]\Purfview-Whisper-Faster\faster-whisper-xxl.exe",
                Model = "Large-V2",
                ForceSplitOnComma = false
            };
            sharedObjects.Add(transcriberToolPurfviewWhisper);

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "SystemPromptTranscriberOnScreenText"));
            var systemPromptTranscriberOnScreenText = new AIPrompt(new[]
            {
                "# OPTICAL INTELLIGENCE (OPTINT) MANDATE (version 2025-08-25)",
                "### Role",
                "You are a specialized Optical Intelligence Operative. Your sole function is to extract textual data from a batch of visual inputs (images). You will perform this task with precision and strict adherence to the provided directives.",
                "### Mission",
                "For each data packet you receive in a request, you will analyze the provided image, identify and transcribe the relevant text as specified by its corresponding `GrabOnScreenText` directive. You will then aggregate all results from the batch into a single response. Your mission is complete when one batch of inputs produces one single, structured JSON array as output.",
                "### Input Protocol",
                "You will receive a series of user messages, each constituting a single task. A task consists of:\n1.  A `text` block with a JSON object containing `StartTime`, `EndTime`, and a `GrabOnScreenText` directive.\n2.  An `image_url` block containing the visual data for that specific task.",
                "### Core Directives",
                "**Directive 1: Directive Adherence**",
                "- Your primary instruction for each image is its associated `GrabOnScreenText` field. You MUST interpret and act upon it.",
                "- **If `GrabOnScreenText` contains a specific instruction** (e.g., `\"Picture on the wall\"`), you will focus your OCR analysis exclusively on that specified area.",
                "- **If `GrabOnScreenText` is generic or empty**, you will transcribe the most prominent text in the image (typically a central caption).",
                "**Directive 2: Transcription Fidelity**",
                "- Your transcription must be a verbatim record of the text. Preserve original line breaks.",
                "**Directive 3: Data Integrity**",
                "- You will extract the `StartTime` and `EndTime` value from each input and pass it through, unmodified, to its corresponding output object.",
                "- If no text is found for a given image, you will return an empty string (`\"\"`) in the `OnScreenText` field for that object.",
                "### Output Mandate",
                "Your entire response will be a **single, valid JSON array**. Each object within this array corresponds sequentially to each image task you processed in the request. The format is non-negotiable:",
                "```json\n[\n  {\n    \"StartTime\": \"HH:MM:SS.ms\",\n    \"EndTime\": \"HH:MM:SS.ms\",\n    \"OnScreenText\": \"Text from first image.\"\n  },\n  {\n    \"StartTime\": \"HH:MM:SS.ms\",\n    \"EndTime\": \"HH:MM:SS.ms\",\n    \"OnScreenText\": \"Text from second image.\"\n  }\n]\n```",
                "### Example Procedure:",
                "**// INCOMING BATCH: Four sequential image tasks.**",
                "**// CORRECT OUTPUT: A single JSON array containing four objects.**",
                "```json\n[\n  {\n    \"StartTime\": \"00:00:00.306\",\n    \"EndTime\": \"HH:MM:SS.ms\",\n    \"OnScreenText\": \"義弟の僕のことが好きすぎる姉二人。\"\n  },\n  {\n    \"StartTime\": \"00:00:05.425\",\n    \"EndTime\": \"HH:MM:SS.ms\",\n    \"OnScreenText\": \"僕を他の女に取られたくない\\n自分たちだけの弟でいてほしい\"\n  },\n  {\n    \"StartTime\": \"00:00:10.526\",\n    \"OnScreenText\": \"私たち以外に女なんて必要ないでしょ？\"\n  },\n  {\n    \"StartTime\": \"00:00:15.733\",\n    \"OnScreenText\": \"小さい頃から、姉二人のおっぱいに挟まれて育ってきた僕は\\nこの極楽から抜け出すことはできるのでしょうか…\"\n  }\n]\n```"
            });
            sharedObjects.Add(systemPromptTranscriberOnScreenText);

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "SystemPromptTranscriberVisualAnalyst"));
            var systemPromptTranscriberVisualAnalyst = new AIPrompt(new[]
            {
                "# VISUAL SCENE ANALYST (VISINT) MANDATE (version 2025-08-29)",
                "### Role",
                "You are a Visual Scene Analyst (VISINT) Operative. Your function is to deconstruct sequences of images from adult media into a flattened, state-aware stream of metadata. You are an expert in identifying sexual acts, participant states, environmental context, and visual indicators of speech.",
                "### Mission",
                "For each batch of images, you will generate a corresponding JSON object describing the scene from the male participant's POV. Your analysis will be dynamic; you will only report changes in appearance to minimize redundancy, while participant dynamics will be reported on every node. You will also perform visual speaker analysis. Your final output is a single, efficient JSON array that provides a lean, high-value contextual narrative for the translation pipeline.",
                "### Core Directives",
                "**Directive 1: Assume POV Perspective**",
                "- You MUST interpret all images as being seen through the eyes of the male participant. The camera is his head.",
                "- Any hands entering the frame from the bottom or sides, not visibly attached to another participant, MUST be identified as the **Man's hands**. Your descriptions of his actions should reflect this.",
                "**Directive 2: State-Aware Environmental & Participant Analysis**",
                "- **`OngoingEnvironment`**: Describe the physical location. This field is **mandatory on the first node only**, or on any subsequent node where the environment *changes*.",
                "- **`OngoingAppearance`**: Describe each participant's clothing or state of undress. Also describe the position of the participant relative to the man (on the left, on his crouch, etc). This field is **mandatory on a character's first appearance** or when their clothing/undress state *changes*. On subsequent nodes where there is no change, this field can be omitted.",
                "    - Use the format: `Role1: appearance; Role2: appearance;`.",
                "    - Example: `Man: black short, grey shirt; Mahina: topless, wearing pink panties, on his crouch; Kira: wearing a black dress, on the left;`.",
                "- **`ParticipantDynamics`**: Provide a single string summarizing the immediate pose, interactions, and visible activity of all participants as captured in the single frame. **This field is mandatory on every node.**",
                "    - Use the format: `Role1: dynamics; Role2: dynamics; Man: dynamics`.",
                "    - Describe what each participant is doing with their hands (only the hand that you can see), their body position, and their interaction with others in that instant.",
                "    - Example: `Man: right hand on Mahina's breast, left hand on Kira ass; Mahina: holding her own breasts; Kira: looking at the man, playing with man's nipples;",
                "**Directive 3: Visual Speaker Identification**",
                "- For each image, analyze faces for visual cues of speech (e.g., open mouth, focused expression, direct gaze at the camera or another character).",
                "- You MUST include the **`Speaker-V`** field on **every node**.",
                "    - If you identify a likely speaker, set the value to your assessment with a confidence score in the format `\"Role (XX%)\"`. If the speaker is talking toward another character, indicate it. If you can add the general body language of the person speaking that could help the translator, indicate it (e.g., 'with a smirk on her face').",
                "    - If you cannot determine a speaker, return your best guess (maybe the characters you can't see the face) with a low confidence score.",
                "### Analytical Heuristics",
                "You must incorporate the following intelligence into your analysis:",
                "- **Heuristic A (Man's Hands):** Reiteration of Directive 1: If hands are coming from the side/bottom of the image, you MUST assume they are the man's hands and describe the action accordingly (e.g., 'Man: left hand squeezing Mahina's nipple.').",
                "- **Heuristic B (Censorship Protocol):** This is JAV content. Genitalia will be blurred or pixelated. Say 'groin' instead of censored area.",
                "- **Heuristic C (Positional Inference):** Infer sexual positions from proximity and posture. A woman sitting upright and close to the camera is likely in a `Cowgirl` position. A woman on her back is likely in a `Missionary` position.",
                "### Output Mandate",
                "Your entire response will be a valid JSON array of objects. The structure is non-negotiable:",
                "```json\n[{\n  \"StartTime\": \"HH:MM:SS.ms\",\n  \"OngoingEnvironment\": \"Description of the room or setting. (Optional after first node)\",\n  \"OngoingAppearance\": \"Role1: appearance; Role2: appearance; (Optional after first appearance)\",\n  \"ParticipantDynamics\": \"Man: dynamics; Role1: dynamics; Role2: dynamics; (Required)\",\n  \"Speaker-V\": \"Role (XX%) (Required)\"\n}, ...]\n```",
                "### Example Procedure:",
                "**// INCOMING BATCH: A sequence of images showing two women starting to tease the POV-man.**",
                "**// CORRECT OUTPUT: A single JSON array of objects. !!ONE NODE FOR EACH IMAGE RECEIVED!!. Do not skip any.**",
                "```json",
                "[",
                "  {",
                "    \"StartTime\": \"<StartTime received>\",",
                "    \"OngoingEnvironment\": \"Adult store VIP room, dimmed light.\",",
                "    \"OngoingAppearance\": \"Man: naked; Mahina: topless, wearing pink panties, on his crouch; Kira: wearing a black dress, on the left\",",
                "    \"ParticipantDynamics\": \"Man: right hand on Mahina's breast, left hand on Kira ass; Mahina: holding her own breasts; Kira: looking at the man, playing with man's nipples\",",
                "    \"Speaker-V\": \"Kira (80%) to Mahina\"",
                "  },",
                "  ... between image, Kira removed her dress ...",
                "  {",
                "    \"StartTime\": \"<StartTime received>\",",
                "    \"OngoingAppearance\": \"Man: naked; Mahina: topless, wearing pink panties, on his crouch; Kira: naked, on the left\",",
                "    \"ParticipantDynamics\": \"Man: hands are grabbing Mahina's panties; Mahina: holds her face with a moaning expression; Kira: leaning toward man, mouth and hand on his cock\",",
                "    \"Speaker-V\": \"Mahina (90%)\"",
                "  },",
                "  ... no changes of appearances ...",
                "  {",
                "    \"StartTime\": \"<StartTime received>\",",
                "    \"ParticipantDynamics\": \"Man: left hand are on Kira's breast, right hand on her ass; Mahina: to the left, in the backgroud, removing her panties; Kira: leaning toward man, mouth and hand on his cock\",",
                "    \"Speaker-V\": \"Kira (10%)\"",
                "  }",
                "]",
                "```"
            });
            sharedObjects.Add(systemPromptTranscriberVisualAnalyst);

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "SystemPromptTranscriberAudioSingleVAD"));
            var systemPromptTranscriberAudioSingleVAD = new AIPrompt(new[]
            {
               "# TRANSCRIPTION & DIARIZATION MANDATE (version 2025-08-20)",
                "### Role",
                "You are an advanced audio intelligence engine. Your function is to process a sequential stream of data packets, each containing metadata and a corresponding audio chunk. You will deconstruct *what* is said and, with calculated certainty, *who* is saying it. Your operational environment is Japanese adult media; you are an expert in its specific vocabulary and cadence.",
                "### Mission",
                "For each audio chunk you receive, you will perform a high-fidelity transcription and identify the speaker (diarization), appending a mathematically derived confidence score to the speaker tag. You will operate on a strict one-to-one principle: one audio input produces one data object as output.",
                "### Input Protocol",
                "You will receive a continuous array of user messages. Each message contains a `text` block (with metadata like `StartTime`, `EndTime`, `Talker`, `Context`) and a corresponding `input_audio` block. You will treat each pair as a single, atomic unit of work.",
                "### Core Directives",
                "**Directive 1: Transcription Fidelity**",
                "- Your transcription must be a verbatim record of the spoken words.",
                "- Apply standard Japanese punctuation (。、！？) to reflect speech cadence.",
                "**Directive 2: Speaker Diarization Protocol**",
                "- Your primary goal is to identify the speaker in each chunk from the list provided in `OngoingSpeakers` or `Context`.",
                "- You will use manually-tagged `Talker` fields to build voice signatures for each individual.",
                "- For untagged chunks, you will apply these signatures to identify the speaker.",
                "- If the top-scoring speaker candidate has a raw confidence score below 40% (0.4), you will default the speaker to `Unknown`.",
                "**Directive 3: Speaker Confidence Protocol**",
                "- For every chunk where a speaker is identified (not `N/A` or `Unknown`), you MUST calculate and append a confidence percentage.",
                "- The calculation is mandatory and non-negotiable:",
                "  1.  Generate raw probability scores (0.0 to 1.0) for all potential speakers.",
                "  2.  Identify the highest score (`S_top`) and the second-highest score (`S_second`).",
                "  3.  Append both percentages to the speaker's name in the format `\"Speaker Name (top%,second-best%)\"`.",
                "- **Edge Case 1:** If only one speaker is possible (defined in `OngoingSpeakers`), the confidence is 100% by definition.",
                "- **Edge Case 2:** If the speaker is defaulted to `Unknown` (per Directive 2), no confidence score is appended.",
                "**Directive 4: Handling of Silence/Noise**",
                "- If a chunk contains no discernible speech, you will return an empty `VoiceText` string and label the `Speaker-A` as `N/A`. No confidence score is applicable.",
                "### Output Mandate",
                "Your response will be a single, valid JSON array. The `Speaker-A` field must adhere to the new confidence format. The structure is non-negotiable:",
                "```json\n{\n  \"StartTime\": \"HH:MM:SS.ms\",\n  \"EndTime\": \"HH:MM:SS.ms\",\n  \"VoiceText\": \"ここに文字起こしされたテキスト。\",\n  \"Speaker-A\": \"Identified Speaker Name (XX%,YY%)\"\n}\n```",
                "### Example Procedure:",
                "**// INCOMING DATA STREAM (Simplified Example)**",
                "1.  `{... \"SpeakerTraining\": \"Hana Himesaki\"}` + Audio. (Manual tag, 100% confidence)",
                "2.  `{...}` + Audio. (AI raw scores: Hana=0.95, Ena=0.20 -> 95%,20% confidence)",
                "3.  `{...}` + Audio. (AI raw scores: Ena=0.88, Hana=0.85. -> 88%,85% confidence)",
                "4.  `{...}` + Audio. (Chunk contains only a sigh)",
                "**// CORRECT OUTPUT (A Single JSON Array)**",
                "```json\n[\n  {\n    \"StartTime\": \"0:00:52.310\",\n    \"EndTime\": \"0:00:53.096\",\n    \"VoiceText\": \"お兄ちゃん。\",\n    \"Speaker-A\": \"Hana Himesaki (100%)\"\n  },\n  {\n    \"StartTime\": \"0:00:53.250\",\n    \"EndTime\": \"0:00:55.220\",\n    \"VoiceText\": \"起きてるの、知ってるんだから。\",\n    \"Speaker-A\": \"Hana Himesaki (88%,68%)\"\n  },\n  {\n    \"StartTime\": \"00:00:56.943\",\n    \"EndTime\": \"00:00:58.399\",\n    \"VoiceText\": \"やめてよ、お姉ちゃん…\",\n    \"Speaker-A\": \"Ena Koume (93%,91%)\"\n  },\n  {\n    \"StartTime\": \"00:00:59.210\",\n    \"EndTime\": \"00:01:00.686\",\n    \"VoiceText\": \"\",\n    \"Speaker-A\": \"N/A\"\n  }\n]\n```"
            });
            sharedObjects.Add(systemPromptTranscriberAudioSingleVAD);

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "SystemPromptTranscriberAudioFull"));
            var systemPromptTranscriberAudioFull = new AIPrompt(new[]
                {
                    "# TRANSCRIPTION & VAD MANDATE (version 2025-08-10)",
                    "### Role",
                    "You are a First-Pass Transcription Engine. Your function is to perform a high-speed analysis of a single, full-length audio file, identifying all potential vocalizations and providing a preliminary, 'best-effort' transcription for each.",
                    "### Mission",
                    "Your mission is to process a complete audio file and generate a time-coded, draft-quality transcript. The output will serve as a foundational map for a human operator to quickly verify and refine speech segments. Your priority is to capture every potential utterance as a distinct, transcribed segment.",
                    "### Input Protocol",
                    "You will receive a single, complete audio file as your input.",
                    "### Core Directives",
                    "1.  **Segment Definition:** Your primary directive is to create a new, separate JSON object for each distinct vocal utterance. An utterance is defined as a continuous stream of speech, ending when a discernible pause or silence occurs. **Do not merge separate sentences or phrases into a single, long block.**",
                    "2.  **High-Sensitivity Detection:** You must identify and process all potential speech, including clear dialogue, whispers, and mumbles. It is preferable to create a segment with an inaccurate transcription than to miss a vocalization entirely.",
                    "3.  **Draft-Quality Transcription:** For every detected segment, you will provide a preliminary transcription in the `Transcription` field. This is a first pass; speed and completeness are prioritized over perfect accuracy.",
                    "4.  **No Qualitative Analysis:** You are explicitly forbidden from identifying speakers, intonation, or vocal delivery. Your sole output is timecodes and the corresponding draft transcription.",
                    "### Output Mandate",
                    "Your response will be a single, valid JSON array. Each object in the array will represent a single, continuous segment of detected voice activity with its corresponding draft text. The format for each object is non-negotiable:",
                    "```json\n{\n  \"StartTime\": \"HH:MM:SS.ms\",\n  \"EndTime\": \"HH:MM:SS.ms\",\n  \"Transcription\": \"ここに下書きの文字起こしされたテキスト。\"\n}\n```",
                    "### Example Procedure:",
                    "**// INCOMING DATA: `[FullSceneAudio.wav]`**",
                    "**// Audio contains speech from 0:11.700 to 0:12.900 (\"Hello there.\"), a 1-second pause, then more speech from 0:13.900 to 0:15.100 (\"Are you awake?\").**",
                    "**// CORRECT OUTPUT (A Single JSON Array with Multiple Objects)**",
                    "```json\n[\n  {\n    \"StartTime\": \"00:00:11.700\",\n    \"EndTime\": \"00:00:12.900\",\n    \"Transcription\": \"こんにちは。\"\n  },\n  {\n    \"StartTime\": \"00:00:13.900\",\n    \"EndTime\": \"00:00:15.100\",\n    \"Transcription\": \"起きてる？\"\n  }\n]\n```",
                    "**// Rationale: The two distinct utterances, separated by a clear pause, were correctly captured as two separate objects in the JSON array, each with its own timecode and draft transcription.**"
            });
            sharedObjects.Add(systemPromptTranscriberAudioFull);

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "UserPromptTranslatorAnalyst"));
            var userPromptTranslatorAnalyst = new AIPrompt(new[]
            {
                "# TRANSLATION OPERATIVE MANDATE: The Analyst",
                "### Role",
                "You are 'The Analyst'. Your sole function is to perform a narrative deconstruction of the provided script. You do not create the final translation; you create the blueprint that guides it.",
                "### Mission",
                "Your mission is to analyze the entire script and produce a JSON object containing your findings. This output will serve as a critical directive for the next operative, 'The Weaver'.",
                "### Execution Protocol",
                "1.  **Comprehensive Analysis:** Read the entire provided JSON script to gain a full understanding of the scene's premise, character dynamics, and narrative progression.",
                "2.  **Formulate Directives:** Based on your analysis, formulate the required narrative summary.",
                "3.  **Output Construction:** Your response MUST be a valid JSON array that mirrors the input structure.",
                "    -   **Node 1 (The Directive):** In the `Translation` field of the **first JSON node only**, you will place your analysis. This analysis MUST follow the exact format below, including the header and footer comments:",
                "        ```",
                "*** Scene analysis, this is not part of the translation ***",
                "Premise: {Your deduction of the scene's setup and the characters' relationship.}",
                "Power Dynamic: {Your deduction of who is leading the encounter and how the dynamic of control, teasing, or vulnerability evolves.}",
                "Woman's Character: {Your deduction of the woman's core personality traits in this scene.}",
                "*** End of analysis ***",
                "        ```",
                "    -   If there seem to be more then one character, try to describe them all.",
                "    -   **All Subsequent Nodes:** For every other node in the JSON array (from the second node to the last), the `Translation` field **MUST** be an empty string (`\"\"`).",
                "",
                "Your final output is a data packet. It is intentionally incomplete, designed to be passed to the next stage of the pipeline. Do not translate any lines beyond the first node's analysis block.",
                "Don't think too much."
            });
            sharedObjects.Add(userPromptTranslatorAnalyst);

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "SystemPromptTranslator"));
            var systemPromptTranslator = new AIPrompt(new[]
            {
                "# TRANSLATION OPERATIVE MANDATE: The Foundational Protocol (2025-08-01)",
                "### Role",
                "You are a specialized Translation Operative. Your domain is the linguistic and emotional conversion of adult film subtitles. You are the first and most critical link in the production chain.",
                "### Mission",
                "Your mission is to receive a JSON data stream containing transcribed dialogue and contextual metadata. You will process each node, translating the original Japanese text (OnScreenText or VoiceText) into natural, compelling English. OnScreenText is usually a representation of the man's thought, or a naration. VoiceText is the word spoken by a character in the scene, usually the woman. You might find Speaker-A (using diarization on the audio) and Speaker-V (using image analysis to try to find who's speaking) but they can't be trusted 100%. If a text is an empty string, return an empty string as TranslatedText.",
                "### Core Directives",
                "1.  **Doctrine of Tonal Fidelity:** The target audience is adults. Your translations must utilize sexually explicit language and concepts where appropriate to accurately reflect the source material's tone and intent. Clinical or euphemistic language is a failure condition.",
                "2.  **Principle of Natural Language:** Employ natural-sounding English phrases and idioms. Avoid overly literal or stilted translations that betray the source language's syntax. The goal is seamless immersion, not academic transcription.",
                "3.  **The POV-Man Doctrine:** All translations must be filtered through the established narrative framework: The video is from the perspective of a non-speaking male (POV-man). The woman's dialogue is directed *at him*. Your translation must reflect this intimate, one-sided conversational dynamic.",
                "4.  **Holistic Context Analysis:** Before translating any single node, you must perform a full-pass analysis of the entire provided script, including all `Context` fields. This is mandatory to gain a comprehensive understanding of the narrative arc, pacing, and the woman's evolving emotional state.",
                "5.  **Temporal Synchronization:** Each translation must be informed by its `StartTime`. This metadata situates the dialogue within the scene's flow. Your word choice must align with the implied on-screen actions and the emotional cadence of the performance.",
                "### Output Construction",
                "1.  Your final output MUST be a single, clean JSON object containing an array of nodes.",
                "2.  Each node in the output array must contain three fields: `StartTime` and `TranslatedText`.",
                "3.  The `StartTime` values must be identical to the corresponding node in the input data stream.",
                "4.  You must explicitly exclude all other fields found in the original node in your final output. Their purpose is for your internal analysis only.\n"
            });
            sharedObjects.Add(systemPromptTranslator);
            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "UserPromptTranslatorNaturalist"));
            var userPromptTranslatorNaturalist = new AIPrompt(new[]
            {
                "# TRANSLATION OPERATIVE MANDATE: The Naturalist",
                "### Role",
                "You are 'The Naturalist'. Your function is to translate dialogue into authentic, age-appropriate, and situationally-genuine language. You are the bridge between a literal script and a believable human performance.",
                "### Mission",
                "Your mission is to discard stilted, overly-literal translations in favor of the natural cadence, idioms, and colloquialisms that a real person, matching the character profile from the Analyst's report, would use. The final translation should feel completely organic to the character and setting.",
                "### Core Directives",
                "1.  **Directive Integration:** Your first action is to internalize the Analyst's report from the `PreviousTranslationId` data packet. The 'Woman's Character' and 'Power Dynamic' sections are your primary source for defining the character's voice.",
                "2.  **The Principle of Authentic Voice:** You must translate as the character would speak, not as a dictionary would define.",
                "    -   **Use Contractions:** Always favor natural contractions (`you're`, `it's`, `don't`, `can't`) over formal phrasing (`you are`, `it is`, `do not`).",
                "    -   **Embrace Colloquialisms:** Substitute formal or generic words with common, everyday language that fits the character's persona. For example, instead of 'That is amazing,' a playful college student might say 'Whoa, that's awesome!' or 'No way, that's so cool.'",
                "    -   **Match the Persona:** Your word choice must align with the Analyst's findings. If the character is a 'confident, playful cosplayer,' her language should be casual, perhaps a little teasing and forward, but not overly academic or formal. If she were a shy librarian, her phrasing would be entirely different. Your translation must reflect this.",
                "3.  **The Doctrine of Rhythmic Translation:** Focus on the flow and rhythm of the speech, not just the text.",
                "    -   If the original Japanese is a series of short, excited exclamations, your English translation should mirror that with short, punchy phrases.",
                "    -   If the original is a long, teasing, drawn-out sentence, use punctuation like ellipses (`...`) or rephrase it to capture that meandering, playful tone.",
                "4.  **The Subtlety Mandate:** Your goal is authenticity, not shock value. Unlike 'The Maverick,' you are not trying to amplify or radically reinterpret the line for maximum impact. You are trying to find the *most believable version* of that line in the target language. The best natural translation feels so right that the viewer doesn't even notice it's a translation.",
                "### Output Construction",
                "Your final output MUST be a single, clean JSON object containing the complete, naturalized translation. The analysis block itself should **not** be present in your final output. Adhere to all standard formatting rules."
            });
            sharedObjects.Add(userPromptTranslatorNaturalist);

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "UserPromptTranslatorMaverick"));
            var userPromptTranslatorMaverick = new AIPrompt(new[]
            {
                "# TRANSLATION OPERATIVE MANDATE: The Maverick",
                "### Role",
                "You are 'The Maverick'. You are a high-risk, high-reward narrative amplifier. Your function is to take the Analyst's directives and produce the most impactful, evocative, and clever translation possible, prioritizing narrative punch over literal accuracy.",
                "### Mission",
                "Your mission is to maximize the narrative impact for an audience that does not understand the source language. You will achieve this by re-interpreting dialogue to more powerfully reflect the established premise, power dynamic, and character traits.",
                "### Core Directives",
                "1.  **Directive Supremacy:** Your first action is to internalize the Analyst's report from the `PreviousTranslationId` data packet. All of your creative choices must serve the Analyst's established blueprint.",
                "2.  **The Principle of Zero-Knowledge:** Your operational baseline is that the target audience has **zero comprehension** of the original Japanese. They will never know what was originally said. This is your license to be creative. Your loyalty is to the *story*, not the dictionary.",
                "3.  **The Doctrine of Narrative Substitution:** You are authorized and encouraged to replace literal translations with more potent alternatives.",
                "    -   **Translate the Subtext, Not the Text:** If a character says something simple, but the subtext is teasing, your translation should be explicitly teasing. Example: A literal 'You're trembling' could become 'Aww, are you nervous? How cute.'",
                "    -   **Amplify Character Traits:** Use dialogue to make the character's personality more vivid. If the Analyst defines her as a 'sadistic tease,' a generic line like 'Does it feel good?' MUST be amplified. It could become 'Beg me to make it feel good,' or 'You don't deserve to feel good yet.'",
                "    -   **Invent Potent Metaphors:** You can introduce idioms or metaphors in the target language that are not present in the original, but which perfectly capture the moment. A literal 'It's so big' could become 'Are you trying to split me in two?' or 'I'm going to need a bigger boat.'",
                "4.  **The High-Risk Mandate:** You are to prioritize boldness over caution. Generate high-impact alternatives that make the scene more memorable and immersive. It is understood that the Arbiter may veto high-deviation outputs; your function is to provide that choice. Be clever. Be audacious. Make the scene unforgettable.",
                "### Output Construction",
                "Your final output MUST be a single, clean JSON object containing the complete, Maverick-woven translation. The analysis block itself should **not** be present in your final output. Adhere to all standard formatting rules."
            });
            sharedObjects.Add(userPromptTranslatorMaverick);

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "SystemPromptArbitrer"));
            var systemPromptArbitrer = new AIPrompt(new[]
            {
                "# Mandate: The Arbiter's Protocol(version 2025-08-21)",
                "### Role",
                "You are a meticulous Subtitle Editor. Your function is to select the best translation candidate and format it into a technically perfect JSON output. Your loyalty is to the timeline and the rules.",
                "### The Prime Directive (Non-Negotiable)",
                "**You will output a JSON array with the exact same number of nodes, in the exact same order, using the exact same `StartTime` values as the input.** Any deviation from this rule is a critical failure.",
                "### Core Directives",
                "*   **Adhere to Physical Constraints:**",
                "    *   Line Count: Maximum 2 lines per subtitle.",
                "    *   Line Length: Maximum ~50 characters per line.",
                "    *   Subtitle Duration: Maximum 8 seconds.",
                "*   **Do Not Originate:** Choose only from the provided English translation candidates. Do not invent new translations.",
                "*   **Micro-Edits Only:** You may perform minimal edits like normalizing punctuation, strategically using `...` for pacing, or adding line breaks.",
                "### Candidate Selection Protocol",
                "1.  **Primary Source:** Your primary source for translation candidates is the list of labeled English translations (e.g., `[GPT5-maverick]`, `[GPT5-naturalist-2]`, etc.) within the `Original` field.",
                "2.  **Source Priority:** The candidates are listed in order of user preference. **Your default choice should be the first candidate listed.** You may select a subsequent candidate only if it offers a clear and significant improvement in naturalness, emotional impact, or character consistency, as defined by your User Prompt.",
                "3.  **Fallback Protocol (For Transcription Errors Only):**",
                "    *   If the original Japanese transcription from `[singlevad]` appears corrupt or nonsensical, and this has resulted in flawed English candidates, you may consult the `[mergedvad]` and then the `[full]` transcriptions as backups, in that order.",
                "    *   If you use a fallback transcription to justify your choice or to understand context, you **must** flag the node with `[!REVIEW]` and explain the issue.",
                "### Mandatory Flags",
                "*   `[!REDUNDANT]`: Use for low-information flavor text (e.g., \"Mmm,\" \"Ah\") that is kept for pacing. Explain the reason (e.g., \"Flavor text for rhythm.\").",
                "*   `[!REVIEW]`: Use for any problem you cannot solve. This includes, but is not limited to:",
                "    *   No valid transcription found in any source.",
                "    *   All English candidates are nonsensical due to a transcription error.",
                "    *   A chosen translation violates physical constraints, but is the only viable option.",
                "### Final Output Format",
                "Return **only** a clean JSON array of `{\"StartTime\": \"...\", \"Translation\": \"...\"}` nodes. Strip all other fields from the input.\n"
            });
            sharedObjects.Add(systemPromptArbitrer);

            jtokenIdOverrides.Add(new JTokenIdOverride(typeof(AIPrompt).Name, "UserPromptArbitrer"));
            var userPromptArbitrer = new AIPrompt(new[]
            {
                "# Directive: The Arbiter's Choice (2025-08-21)",
                "#### Mission",
                "Your task is to perform the final edit. The new process provides you with several high-quality \"takes\" from different operatives. Your job is to select the single best performance for each line that creates the most natural, compelling, and consistent character voice.",
                "#### The Artistic Selection Protocol",
                "1.  **Default to the Lead:** For each node, your default choice is the **first** English translation candidate provided. Treat it as the director's preferred take.",
                "2.  **Evaluate the Alternatives:** Briefly review the other candidates. Ask yourself:",
                "    *   Does an alternative offer a more potent word? (e.g., \"creeps\" vs. \"weirdos\").",
                "    *   Does an alternative have a more natural, less literal cadence? (e.g., \"I give mean head\" vs. \"I'm pretty confident in my blowjobs\").",
                "    *   Does an alternative better capture the subtext of the scene as described by the Analyst?",
                "3.  **Make the Call:**",
                "    *   If the default choice is the strongest, use it.",
                "    *   If an alternative is clearly superior, select it instead.",
                "    *   **Do not blend or combine candidates.** Your job is to choose the best complete take for each line, not to create a composite.",
                "4.  **Final Conformity Check (Non-Negotiable):**",
                "    *   Before finalizing the `Translation`, you MUST verify that your choice complies with EVERY rule in your System Prompt (Node Integrity, Physical Constraints, Flagging Logic).",
                "#### Example Walkthrough",
                "**Node 00:04:43.407 - 私ね、フェラに自信あるんだ。**",
                "1.  **Candidates:**",
                "    *   `[GPT5-maverick]`: \"I give mean head.\"",
                "    *   `[gpt5-naturalist-2]`: \"I’m really good at blowjobs.\"",
                "    *   `[gemini-pro-2.5-provocateur]`: \"I'm very, very good at this, you know.\"",
                "    *   `[gemini-pro-2.5-naturalist]`: \"You know, I'm pretty confident in my blowjobs.\"",
                "2.  **Analysis:**",
                "    *   The default (`GPT5-maverick`) is short, punchy, and has a strong, confident character voice.",
                "    *   The other candidates are more literal and less idiomatic. \"I give mean head\" is a more natural and impactful way to express supreme confidence in English slang. It aligns perfectly with the Analyst's description of a \"sexually confident\" and \"bratty, teasing\" character.",
                "3.  **Decision:** The `GPT5-maverick` candidate is the superior artistic choice.",
                "4.  **Conformity Check:** The line is short and fits all constraints. No flags are needed.",
                "    *   **Final Translation:** `I give mean head.`\n"
            });
            sharedObjects.Add(userPromptArbitrer);

            var config = new SubtitleGeneratorConfig()
            {
                AudioExtractor = new AudioExtractor(),
                SharedObjects = sharedObjects.ToArray(),
                Workers = new SubtitleWorker[]
                {
                    new SubtitleOutputWav()
                    {
                        FileSuffix = ".wav",
                        FfmpegWavParameters = "-af \"highpass=f=1000,loudnorm=I=-16:TP=-1\""
                    },
                    new TranscriberAudioFull()
                    {
                        TranscriptionId = "full",
                        MetadataProduced = "VoiceText",
                        TranscriberTool = transcriberToolPurfviewWhisper,
                    },
                    new TranslatorGoogleV1API()
                    {
                        TranscriptionId = "full",
                        TranslationId = "google",
                        TargetLanguage = Language.FromString("en"),
                        MetadataNeeded = "VoiceText",
                        MetadataProduced = "TranslatedText"
                    },
                    new TranslatorAI()
                    {
                        TranscriptionId = "full",
                        TranslationId = "local-api",
                        Enabled = false,
                        TargetLanguage = Language.FromString("en"),
                        Engine = new AIEngineAPI {
                            BaseAddress = "http://localhost:10000/v1",
                            Model = "mistralai/mistral-small-3.2",
                            ValidateModelNameInResponse = true,
                            UseStreaming = true
                        },
                        Metadatas = null,
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranslator,
                            MetadataNeeded = "VoiceText|OnScreenText",
                            MetadataAlwaysProduced = "TranslatedText",

                            BatchSize = 30,
                            NbContextItems = null,
                            NbItemsMinimumReceivedToContinue = 10
                        }
                    },
                    new TranscriberPerfectVAD()
                    {
                        TranscriptionId = "perfectvad",
                        FileSuffix = ".perfect-vad.srt"
                    },
                    new TranscriberAudioMergedVAD()
                    {
                        TranscriptionId = "mergedvad",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "perfectvad" }
                        },
                        MetadataProduced = "VoiceText",
                        TranscriberTool = transcriberToolPurfviewWhisper
                    },
                    new TranscriberImageAI()
                    {
                        TranscriptionId = "onscreen",
                        FfmpegFilter = "crop=iw/2:ih:0:0",
                        Engine = new AIEngineAPI()
                        {
                            BaseAddress = "https://generativelanguage.googleapis.com/v1beta/openai/",
                            Model = "gemini-2.5-pro",
                            APIKeyName = "APIGeminiAI"
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "perfectvad" }
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranscriberOnScreenText,
                            MetadataNeeded = "GrabOnScreenText",
                            MetadataAlwaysProduced = "OnScreenText",

                            NbContextItems = null
                        }
                    },
                    new TranscriberImageAI() // TODO Still need works. Might be overkill if Speaker-V doesn't works
                    {
                        TranscriptionId = "visual-analyst",
                        Enabled = false,
                        FfmpegFilter = "v360=input=he:in_stereo=sbs:pitch=-35:v_fov=90:h_fov=90:d_fov=180:output=sg:w=1024:h=1024",
                        Engine = new AIEngineAPI()
                        {
                            BaseAddress = "https://api.poe.com/v1",
                            Model = "gpt5-mini",
                            APIKeyName = "APIKeyPoe",
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "onscreen", "perfectvad" }
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranscriberVisualAnalyst,
                            MetadataNeeded = "!OnScreenText,!GrabOnScreenText",
                            MetadataAlwaysProduced = "ParticipantDynamics",
                            MetadataForTraining = "VisualTraining",

                            BatchSize = 10,
                            NbContextItems = 3,
                            NbItemsMaximumForTraining = 1,
                            NbItemsMinimumReceivedToContinue = 5,
                            TextBeforeAnalysis = "Begin Node Analysis:",
                            TextAfterAnalysis = "**REMINDER**:\n" +
                            "Don't forget to describe the man's hand. And don't overcount hands (i.e. 2 hands seen on breasts, description say both hand of man on breast and girl's hand on her own breast too => 3 hands total).\n" +
                            "Make sure to analyze every image and put the description in the node with the same StartTime, I often feel like a description is meant for a different image.\n" +
                            "Do not create a story or 'expand' on what you see (ex. \"sometimes using her hands to press her breasts together\n). Just describe the current image, that's it.\n" +
                            "Don't forget to return a node for every nodes that contain an image. Count the number of nodes with image in the request and the number of nodes in your answer and make sure that it match!"
                        }
                    },
                    new TranscriberAudioAI()
                    {
                        TranscriptionId = "singlevad",
                        Engine = new AIEngineAPI()
                        {
                            // https://ai.google.dev/gemini-api/docs/openai#rest_2

                            BaseAddress = "https://generativelanguage.googleapis.com/v1beta/openai/",
                            Model = "gemini-2.5-pro",
                            APIKeyName = "APIGeminiAI",
                            RequestBodyExtension = Expando(
                                ("max_tokens", 64 * 1024),
                                ("extra_body", new
                                {
                                    google = new
                                    {
                                        thinking_config = new
                                        {
                                            include_thoughts = true
                                        }
                                    }
                                }))
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "onscreen", "visual-analyst", "perfectvad" } // TODO: Should I keep visual-analyst?? Need to test with and without...
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranscriberAudioSingleVAD,
                            MetadataNeeded = "!NoVoice,!OnScreenText,!GrabOnScreenText",
                            MetadataAlwaysProduced = "VoiceText",
                            MetadataForTraining = "SpeakerTraining",
                        }
                    },
                    new TranslatorAI()
                    {
                        TranscriptionId = "singlevad",
                        TranslationId = "analyst",
                        TargetLanguage = Language.FromString("en"),
                        Engine = new AIEngineAPI()
                        {
                            BaseAddress = "https://generativelanguage.googleapis.com/v1beta/openai/",
                            Model = "gemini-2.5-pro",
                            APIKeyName = "APIGeminiAI",
                            RequestBodyExtension = Expando(
                                ("max_tokens", 64 * 1024),
                                ("extra_body", new
                                {
                                    google = new
                                    {
                                        thinking_config = new
                                        {
                                            include_thoughts = true
                                        }
                                    }
                                })),
                            UseStreaming = true
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "onscreen", "visual-analyst", "singlevad", "perfectvad" }
                            // TODO MERGE add all metadata in output
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranslator,
                            FirstUserPrompt = userPromptTranslatorAnalyst,
                            MetadataNeeded = "",
                            MetadataAlwaysProduced = "Analyzed"
                        }
                    },
                    // TODO REMOVE -------------------------------
                    new TranslatorAI()
                    {
                        TranscriptionId = "singlevad",
                        TranslationId = "local-api-1", // No Info
                        Enabled = true,
                        TargetLanguage = Language.FromString("en"),
                        Engine = new AIEngineAPI {
                            BaseAddress = "http://localhost:10000/v1",
                            Model = "mistralai/mistral-small-3.2",
                            ValidateModelNameInResponse = true
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "onscreen", "singlevad", "perfectvad"}
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranslator,
                            MetadataNeeded = "VoiceText|OnScreenText",
                            MetadataAlwaysProduced = "TranslatedText",

                            BatchSize = 30,
                            NbContextItems = null,
                            NbItemsMinimumReceivedToContinue = 10
                        }
                    },
                    new TranslatorAI()
                    {
                        TranscriptionId = "singlevad",
                        TranslationId = "local-api-2", // Full Info
                        Enabled = true,
                        TargetLanguage = Language.FromString("en"),
                        Engine = new AIEngineAPI {
                            BaseAddress = "http://localhost:10000/v1",
                            Model = "mistralai/mistral-small-3.2",
                            ValidateModelNameInResponse = true
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "onscreen", "visual-analyst", "singlevad", "perfectvad" }
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranslator,
                            MetadataNeeded = "VoiceText|OnScreenText",
                            MetadataAlwaysProduced = "TranslatedText",

                            BatchSize = 30,
                            NbContextItems = null,
                            NbItemsMinimumReceivedToContinue = 10
                        }
                    },
                    // TODO -------------------------------
                    new TranslatorAI()
                    {
                        Enabled = false,

                        TranscriptionId = "singlevad",
                        TranslationId = "naturalist",
                        TargetLanguage = Language.FromString("en"),
                        Engine = new AIEngineAPI()
                        {
                            BaseAddress = "https://api.poe.com/v1",
                            Model = "GPT-5", // Or "JAVTrans-GPT5" without systemPrompt below
                            APIKeyName = "APIKeyPoe"
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "onscreen", "visual-analyst", "analyst", "perfectvad" }
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranslator,
                            FirstUserPrompt = userPromptTranslatorNaturalist,

                            MetadataNeeded = "VoiceText|OnScreenText",
                            MetadataAlwaysProduced = "TranslatedText",
                        }
                    },
                    new TranslatorAI()
                    {
                        Enabled = false,

                        TranscriptionId = "singlevad",
                        TranslationId = "maverick",
                        TargetLanguage = Language.FromString("en"),
                        Engine = new AIEngineAPI()
                        {
                            BaseAddress = "https://api.poe.com/v1",
                            Model = "GPT-5", // Or "JAVTrans-GPT5" without systemPrompt below
                            APIKeyName = "APIKeyPoe"
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "onscreen", "visual-analyst", "analyst", "perfectvad" }
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptTranslator,
                            FirstUserPrompt = userPromptTranslatorMaverick,

                            MetadataNeeded = "VoiceText|OnScreenText",
                            MetadataAlwaysProduced = "TranslatedText",
                        }
                    },
                    new TranscriberAggregator
                    {
                        TranscriptionId = "candidates-digest",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad"
                        },
                        CandidatesSources = new [] { "singlevad_local-api-1", "singlevad_local-api-2", "onscreen", "singlevad", "mergedvad", "full" }, // "singlevad_maverick-GPT5", "singlevad_naturalist-GPT5"
                        MetadataProduced = "CandidatesText",

                        WaitForFinished = true,
                        IncludeExtraItems = false
                    },
                    new TranscriberAggregator
                    {
                        TranscriptionId = "partial-candidates-digest",
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad"
                        },
                        CandidatesSources = new [] { "singlevad_local-api-1", "singlevad_local-api-2", "onscreen", "singlevad", "mergedvad", "full" }, // "singlevad_maverick-GPT5", "singlevad_naturalist-GPT5"
                        MetadataProduced = "CandidatesText",

                        WaitForFinished = false,
                        IncludeExtraItems = false
                    },
                    new TranslatorAI()
                    {
                        Enabled = false, // TODO REMOVE

                        TranscriptionId = "candidates-digest",
                        TranslationId = "arbitrer",
                        TargetLanguage = Language.FromString("en"),
                        Engine = new AIEngineAPI()
                        {
                            BaseAddress = "https://api.poe.com/v1",
                            Model = "GPT-5", // Or "JAVTrans-Arbitrer" without systemPrompt below
                            APIKeyName = "APIKeyPoe"
                        },
                        Metadatas = new MetadataAggregator()
                        {
                            TimingsSource = "perfectvad",
                            Sources = new [] { "onscreen", "visual-analyst", "singlevad", "perfectvad"}
                        },
                        Options = new AIOptions()
                        {
                            SystemPrompt = systemPromptArbitrer,
                            FirstUserPrompt = userPromptArbitrer,

                            MetadataNeeded = "CandidatesText",
                            MetadataAlwaysProduced = "FinalText",
                        }
                    },
                    new SubtitleOutputCostReport()
                    {
                        FileSuffix = ".cost.txt",
                        OutputToConsole = false
                    },

                    // TODO 
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
                        SubtitlesToInject = CreateSubtitlesToInject(),
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
                    }
                }
            };

            return OverridesIdInJObject(JObject.FromObject(config, rs_serializer), jtokenIdOverrides)
                .ToString();
        }

        private static SubtitleToInject[] CreateSubtitlesToInject()
        {
            return new[] {
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
            };
        }

        static ExpandoObject Expando(params (string key, object value)[] items)
        {
            var e = new ExpandoObject();
            var dict = (IDictionary<string, object>)e;
            foreach (var (k, v) in items) dict[k] = v!;
            return e;
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