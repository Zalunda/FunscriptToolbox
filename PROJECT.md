# FunscriptToolbox - Architecture & Tools Overview

`FunscriptToolbox` is a modular suite of .NET / C# command-line tools, libraries, and a web UI designed for funscript authoring, audio-based synchronization, video retiming, automated AI-powered subtitle generation/translation, and motion-vector assisted tracking in OpenFunscripter (OFS).

---

## Solution Projects Overview

| Project | Type | Purpose |
| :--- | :--- | :--- |
| **`FunscriptToolbox`** | C# Console App | Main CLI entry point exposing all verb-based tools and commands. |
| **`FunscriptToolbox.Core`** | C# Class Library | Domain models and file format parsers (`Funscript`, `SubtitleFile`, `WIPFunscriptActionCollection`, etc.). |
| **`AudioSynchronization`** | C# Class Library | Acoustic fingerprinting and waveform cross-correlation engine for timeline alignment. |
| **`MotionVectorsExtractor`** | C++ Native Project | Native high-performance motion vector decoder/extractor interfacing with FFmpeg. |

---

## CLI Tools & Verbs Reference

All tools are accessible via `FunscriptToolbox.exe <verb> [options]`.

### 1. Installation & Environment Setup
* **`installation`** *(alias: `inst`)*
  - Scaffolds ready-to-run workflow folders populated with helper batch scripts and configuration templates:
    - `FSTB-CreateSubtitles2025/`: Subtitle transcription and translation workflows (`Staging`, `ManualHQWorkflow`, `AutomaticHQWorkflow`).
    - `FSTB-PrepareScriptForRelease/`: Script packaging and signature generation for releases.
    - `FSTB-VerifyDownloadedScripts/`: Automated verification of funscripts against local video files.

---

### 2. Audio Synchronization (`audiosync`)
Enables cross-release synchronization and drift detection by generating and comparing acoustic fingerprints (`.asig` files).

* **`audiosync.createaudiosignature`** *(alias: `as.cas`)*
  - Extracts the audio waveform from a video and generates an audio signature file (`.asig`), or embeds the signature directly into an existing `.funscript`.
  - Supports batch processing and recursive directory scanning (`-r`).

* **`audiosync.createfunscript`** *(alias: `as.cfs`)*
  - Re-synchronizes a funscript created for one video release/cut to match a different target video release using their respective audio signatures.
  - Allows exporting an offset timeline mapping (`--dumpoffsets` -> `.offsets.json`).

* **`audiosync.verifyfunscript`** *(alias: `as.vfs`)*
  - Compares the embedded audio signature in a `.funscript` against a local video file to verify synchronization.
  - With `--fix`, automatically shifts/adjusts the actions to realign the funscript with the video.

---

### 3. Video Retimer (`retimer`)
Automates variable-speed video compression (e.g. fast-forwarding through dead time or non-action segments while preserving normal speed during dialogue or funscripted actions).

* **`retimer.generatecontrol`** *(alias: `retimer.gen`)*
  - Analyzes existing subtitles (`.srt`) and funscripts (`.funscript`) alongside a video to generate a `.control.srt` timeline blueprint.
  - Detects speech density, isolated utterances, and gap thresholds to mark sections as controlled (normal playback) or gap/fast-forward.

* **`retimer.rendervideo`** *(alias: `retimer.render`)*
  - Uses FFmpeg to render the variable-speed video based on the `.control.srt` blueprint.
  - Produces the output retimed video (`.retimed.mp4`) and an exact time transformation mapping (`.offsets.json`).

* **`retimer.syncassets`** *(alias: `retimer.sync`)*
  - Uses the `.offsets.json` map to remap and warp all original sidecar files (`.srt`, `.funscript`) so they perfectly align with the new variable-speed retimed video.

---

### 4. Subtitles & AI Transcription Pipeline (`subtitles`)
A modular pipeline driven by JSON/C# configuration files to transcribe, identify speakers, and translate video audio into clean `.srt` subtitles.

* **`subtitles.create`** *(alias: `sub.create`)*
  - Processes single video files or multi-part video sequences (`.vseq` or `--autovseq`).
  - Executes a worker pipeline configured via `--config`:
    - **Audio Extraction**: Demuxes audio streams.
    - **VAD & Segmentation**: Voice Activity Detection to isolate dialogue chunks.
    - **Transcription**: Interfaces with Purfview Faster-Whisper, standalone Whisper, or LLM-based transcribers.
      - `TranscriberAudioFullAIGoogleTranscribe` (the step `full-transcribe` of the main configuration, disabled: being tested, `full-ai` is still a `TranscriberAudioFullAI`; if it replaces the type of `full-ai` (same id), what scenes already did is kept: a `TranscriberAudioFullAI.JobState` left by a partial run is continued): the transcription model of Google (`gemini-3.5-transcribe`) through `AIEngineAPIGoogle.Transcribe` (`Infra/AIEngineAPIGoogle.Transcribe.cs`: the Interactions API, `POST {BaseAddress}/interactions`, the audio uploaded with the Files API then deleted; verbatim, the start / end of each word, optional diarization; no batch mode: the engine needs `UseBatchMode: false`; cost from the `usage` tokens). The language: the one of the transcription (`SourceLanguage`, "ja" => "ja-JP"). Chunks of `MaxChunkDuration` (at most 30 min with word times) with the 10 s buffer zone of `TranscriberAudioFullAI`; the subtitles are built from the words (a new one after `MaxGap`, 。？！?!, `MaxDuration` / `MaxChars`, a speaker change), each keeps its words, the ends are moved `EndPadding` later (the model's word ends are early). Measured on MDVR-094 against the reviewed timings: starts ~+0.1 s (85 % within 0.2 s), ends ~0.2 s early.
    - **Speaker Diarization / Identification**: Distinguishes dialogue by speaker.
    - **Translation**: Translates text using Google Translate API or AI/LLMs.
    - **Output Formatting**: Generates clean `.srt` subtitle files, cost reports, and audio signatures.
  - **`--ui <port>`** (default 5055, or the port of `--runasserver`; there is no console mode anymore): all the videos/vseq are processed in parallel (one thread + one `SubtitleGeneratorContext` each) and the progress is shown on a web page (`SubtitlesVerbs/Progress/`: `WebUiServer` on `http://localhost:<port>/`, the browser is opened only with `--openbrowser`). The page shows a tree (video => worker => AI request) with live status and log (the requests / responses sent to the AIs: in the 'Files' tab of the scene, with `--verbose`); AI requests have 2 stop buttons: 'Stop after this request' (the response is received and saved, then the worker stops: `ITaskNode.IsStopAfterRequested`, checked by the request loops) and 'Stop now!' (aborts / cancels the batch: `ITaskNode.ConsumeCancelRequest`; the page asks to confirm when it may still be billed: `SetStopNowWarning`). The other long tasks (ffmpeg, waits, title card detection) have a single 'Stop'. Shared locks (`SubtitlesVerbs/Progress/ResourceLock.cs`): only one "local resource" user at a time (ffmpeg, Purfview whisper, title card detection, AI engines with `UseLocalResourceLock: true`) and only one prompt (JSON correction, pauses) at a time. Everything written goes through `IUserOutput` (`WebUiOutput`).
  - **Configuration** (`SubtitleGeneratorConfigLoader.LoadHierarchically`): the main `--FSTB-SubtitleGenerator.config`, then, in each folder from the main one down to the video's folder, `<name>.override.config` (from the installation, replaced when the user starts from a new version) and then `<name>.override.user.config` (the user's own changes, never created / replaced by the installation, even with `--override`). The `GlobalShared` section is only allowed in the files of the main folder (`LoadGlobalShared`).
  - **Cost of a step** in the 'Flow' view: a pill on the box of each worker with AI calls (`SceneFlowInspector.CountItems`, key `w<n>`), from the costs kept in the transcriptions / translations it produces (`OutputCostReport.GetAICostByWorker`). The rules of the estimate (`Cost.IsAI`, `Cost.IsFallback`, `Cost.Dollars`) are shared by the 'costs' report, the cost of the scene and the steps. The costs of a transcription / translation that no worker produces anymore (an old id) are only in the scene total.
  - **'Config' tab** of the inspector of the 'Flow' view (`Infra/SceneConfigView.cs`, `/api/flowconfig/<scene>?key=w<n>`): the configuration of a step, read again from the files with `LoadHierarchicallyWithSources` (each JToken of the merged configuration gets a `ConfigSource` annotation, its level: `0` main, `0-S` / `0-U` the standard override (`.override.config`) / user override (`.override.user.config`) of the main folder, `1-R` (a full configuration that replaces everything) / `1-S` / `1-U` the files of the first sub-folder..., and a `ConfigSourceHistory` with the values it replaced; Newtonsoft.Json keeps the annotations when it clones a token). The tree walks the merged JSON with the contracts of the serializer: the values written nowhere are the defaults of the code, the ones no class has are flagged 'not used'. A value with no history written in an override file is shown `+1-S` (first defined there: not an override; hidden by 'Overrides only'). A value can be changed (✎, `POST /api/flowconfig/<scene>`, `SceneConfigView.ChangeValue`): written in (or removed from) a `.override.user.config` only (the other files are replaced by a new version), in the entry of the step (`Workers`, by its id; a value in a shared object, for this step only: a `$ref` with other properties) or in the shared object (`SharedObjects`, by its `$id`: every step that uses it). Not in a list (a list is replaced as a whole by an override). The value is checked against the type of the setting; the file is rewritten (comments lost, the previous one in `.bak`), then the configuration is read again: refused (file put back) when it can't be read anymore, and a warning when the value used is not the one written (a file read after it has the setting too). A remove looks for the value in the step first, then in the shared objects (innermost first): '↶' on a value written in a `.override.user.config` removes it from that level, back to the value of the level before (the last item of its history), one level per click. A property whose value is an object (`Node.ObjectChoices`, ex. `Engine`, `RefineTiming.Engine`, the prompts) can get another object: a shared object of a type that fits its declared type (`choices` of the result; not the placeholders '…WillBeOverriden'), `null`, or a new object (a `$type` known by the binder, or a copy of a shared object): in place, or, with an id, a new shared object added at the end of `SharedObjects` of the same file, and a `$ref` to it. No box selected in the graph: 'Items' is greyed and 'Config' shows the whole configuration of the scene (`key=all`, `SceneConfigView.WholeConfigKey`: the merged configuration from its root, all the steps and the shared objects). A value changed there is written in its 'parent' (`SceneConfigWriter.GetWholePlace`): the item of `Workers` (by its id) or of `SharedObjects` (by its `$id`) it's in, like a step in its own view, otherwise the root of the file (ex. `Global`). An item of these lists can't be replaced as a whole.
  - **Configuration files watched** (`ExecuteAsync` of `VerbSubtitlesCreate`): a `FileSystemWatcher` on the folder of the main configuration (sub-folders too) for the main file and the `.override.config` / `.override.user.config` files; 1 s after the last change, the scenes under the folder of a changed file are loaded again (once per folder) and their `MergedConfigHash` compared with the one they were started with (`WebUiScene.ConfigHash`): `WebUiOutput.SetSceneConfigChange` ("changed", the error, or null: back as it was). In the page, a ⚙ on the scene, and a scene that is done goes in the section 'config changed' (right click on its title: 'Reload all' = Retry of each one). A running scene is not stopped: it moves there once done. `GlobalShared` and the `.private.config` are still read only at the start.
  - **'Files' tab** of a scene (`Infra/SceneFilesView.cs`, `ISceneFilesView`, set on the scene node at the start of `ProcessSequence`; `/api/scenefiles/<scene>`: the list, `?folder=scene|backup&name=<name>`: a file): the files next to the video whose name is the container's or a part's name + '.', the `--FSTB-SubtitleGenerator*` files of that folder, and all the files of the backup folder (`WIP.BackupFolder`, known once the .wipsubtitles is loaded). Type / group / date from the name (`yyyyMMddHHmmss_` of the run, `-Req.json` / `-Resp...` of the AI engines, `-<12 hex>` of `SoftDelete`). A file is served only when its folder kind + exact name is in the list (never a path from the page); a text has its base64 runs elided and is cut after 3 M chars. The AI requests / responses are only on disk with `--verbose` (`CreateVerboseTextFile`); they are not kept in memory anymore.

---

## Timing refiner engines (manual-input-preparer)

`RefineTiming` (`TimingRefinerSettings`) snaps the boundaries of each subtitle on the terrain of the audio, then refines them with its `Engine` (shared objects of the default config, chosen with a `$ref`): `TimingRefineEngineNone` (no AI), `TimingRefineEngineVerify` (one request per subtitle), `TimingRefineEngineStartEndJudge` (Start / End passes + optional End tail check), `TimingRefineEngineSpokenMarkers` (see below).
A `$ref` can carry other properties (ex. `{ "$ref": "TimingRefineEngineSpokenMarkers", "Runs": 3 }`): `SubtitleGeneratorConfigLoader.ExpandReferencesWithOverrides` replaces it, after all the overrides are merged, by a copy of the shared object (its `$id`s removed) with these properties merged in; the shared object is not changed. In a merge, an override with another `$ref` / `$type` replaces the object instead of being merged in it (`IsAnotherObject`).
- `TimingRefineEngineSpokenMarkers`: English number words (`SpokenMarkers`: 12 long labels, embedded .wav resources in `Refiner/Markers/`, 16 kHz) are INSERTED in the clip of each subtitle at the start dips around its snapped Start and the end dips around its snapped End (`TimingClipOption.Inserts`, the audio built by `SpokenMarkers.BuildClipAudio`); the AI (prompt `TimingSpokenMarkersSystemPrompt`, answer `MarkedTranscription`, batches of 50) transcribes the clip with the markers inline and the line between « and ». Start = the last marker before «, End = the first after »; asked `Runs` times (other random labels), decided after the last run (`OnDisagreement`: Wider / Majority), silence trimmed, Ends capped by the next Starts; writes `TimingCandidate.TimingConfidence` (the `[TimingConfidence:x]` line). Flags: `UnsureStart` / `UnsureEnd`
(a side not confirmed by every run, or a single run, or an End capped: two codes, so one side can be ignored), `MissingStart` / `MissingEnd` (the line starts before
/ ends after every marker in a run: set to the outermost option). No split of the long subtitles (`CanSplitLongSubtitles` = false). The experiments that led to it: `Refiner/scratchbook/` (git-ignored, README.md).
- `TimingRefiner`: what is common to all the engines (the candidates, the terrain and the snap, the silence trimming, the overlaps, the stats).
- `TimingRefineEngine` (abstract): the building blocks (clips + silence padding, dips, hard limits, bleed text, reading an answer, the cache key `BuildCacheKey`). An engine implements `UsesAI`, `InnerOptionsUsed`, `CreatePasses` (+ `Validate`, capabilities); its passes (`TimingRefinePass`: `Begin` / `PrepareNextRound` / `ProcessResults` / `End`, new objects for each refiner: they hold the state of a run) say which answer fields are cached (`CachedMetadataNames`).
- A new engine: a subclass (no serializer change: the base type is registered), added as a shared object in `SubtitleGeneratorConfigExample`, its tunable settings exposed in the `SharedObjects` of the workflow overrides.

## Interactive review (web UI)

There is no WPF / desktop dialog anymore: everything the user does is in the web UI (`SubtitlesVerbs/Progress/ui.html`).
The **Review editor** (formerly the Timing editor: the code keeps that name, `SceneTimingEditor`, `ITimingEditor`, `/api/timing/`, the `fstTiming...` browser settings) (`TranscriberInteractifReview` + `SubtitlesVerbs/Infra/SceneTimingEditor.cs`) reviews the manual-input file in passes:
the timings (the start and the end of each subtitle are finalized on their own, `ReviewItem.StartFinalized` / `EndFinalized`; "timing validated" = both), the review warnings written by `TranscriberAIManualInputPreparer`
(`[REVIEW:<Code>[:<Detail>]] message` lines, checked one by one: `[CHECKED:...]`), and the speakers (`{OngoingSpeakers:...}` / `{Speaker:...}`).
Its list can be sorted by any column (the arrows follow the order of the list). The `Conf.` column reads a `[TimingConfidence:0.42] reason` line
(0 = surely wrong .. 1 = surely right; a text line like the review warnings, not a `{metadata}`): the line a timing refine engine should write.
The list shows only the kind of review warning chosen in the Warnings tree (none: all), and the subtitles of the filter chosen above it (`S.listFilter`, `filterMatch`):
All, Timing (not validated; in a session: something to finalize in it), Speakers (not known), Visual training (someone present without a
`{VisualTraining:Name (description), Name 2 (description)}` anywhere; the most people first), Audio training (its speaker without an `{AudioTraining:Name}`,
or not known; the longest first). The blocks show what is done (the training: speakers with one / speakers). Adding a VisualTraining / AudioTraining property:
a description per speaker / the speaker talking (the popup of the properties). A **session** (`Ctrl+Enter`) goes through the subtitles of the list,
in its order, to finalize their starts and / or ends, one edge at a time (the start, then the end), played in a short loop at the edge (`sessionSegments`, quieter
outside the subtitle); the longer pieces are played once when going on (`sessionIntro`): the start finalized = from the start to the end; the end finalized and the next one
to do is the start of the next subtitle = from the end to it (far: 1 s after the end, then from 1 s before the next one). The edge is written on the waveform (START / END). The edge is moved by a few ms or with a click / a drag on the waveform, then `Tab` = finalized and the next edge to do (not Left Shift alone anymore: pressed several times, Windows offers the 'Sticky Keys'; a session action held down is done once) (`Space` pauses the loop: finalized while paused,
the edge goes first where it was paused). The keys of `SESSION_ACTIONS` are looked at first during a session (they can be the keys of other actions outside); a modifier
pressed and released alone is a key of its own (`Ctrl`, `LeftShift`, `RightShift`). Ctrl alone plays the whole subtitle once, with the pre-roll and the post-roll.
The tools are in the floating panel of the session, or of the **free form** session (a button of the session dialog: all the tools, nothing guided): its sections
(timing / playback, speakers, properties, edit) can be reordered (drag) and hidden (not the speakers); the rest is in the keys, the right click menu
and the Settings tab. In a session, the view puts the edge being finalized at 30 % of the width of the screen (`sessionFocusView`), when the subtitle / the edge changes.
The tabs under the video (Subtitle: the text (editable), the speakers, the properties, the review warnings; Warnings, Settings, Keys) are closed until clicked.
The sections of the panel and of the Subtitle tab are groups (`SEC_GROUPS`, `secsHtml`): reordered by dragging their title, hidden, remembered. The speakers (`.te-spkq`)
and the properties (`.te-propq`: chips, with the speakers ones; ＋ / click = a name / value popup) are drawn in each place they are shown, their events delegated.
The bars between the 2 sides, the video / the tab and above the waveform are dragged (`splitter`, remembered).
The video played is chosen (FAST = its light video, the default / original: `video?part=&original=1`). A speaker can have an image (📷 of the speakers: the frame
of the video, a rectangle drawn on it; a JPEG data URL in `ReviewSpeaker.Image`, saved with the speakers in the .wipsubtitles), shown when the mouse is on its name. The terrain is not drawn (only used by Next speech / Add);
the bar under the waveform is the progress (the subtitles: validated / half finalized / to do).

