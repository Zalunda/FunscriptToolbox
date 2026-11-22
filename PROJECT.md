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
    - **Speaker Diarization / Identification**: Distinguishes dialogue by speaker.
    - **Translation**: Translates text using Google Translate API or AI/LLMs.
    - **Output Formatting**: Generates clean `.srt` subtitle files, cost reports, and audio signatures.
  - **`--ui <port>`** (default 5055, or the port of `--runasserver`; there is no console mode anymore): all the videos/vseq are processed in parallel (one thread + one `SubtitleGeneratorContext` each) and the progress is shown on a web page (`SubtitlesVerbs/Progress/`: `WebUiServer` on `http://localhost:<port>/`, the browser is opened only with `--openbrowser`). The page shows a tree (video => worker => AI request) with live status, log, and details (requests/responses sent to the AIs); AI requests have 2 stop buttons: 'Stop after this request' (the response is received and saved, then the worker stops: `ITaskNode.IsStopAfterRequested`, checked by the request loops) and 'Stop now!' (aborts / cancels the batch: `ITaskNode.ConsumeCancelRequest`; the page asks to confirm when it may still be billed: `SetStopNowWarning`). The other long tasks (ffmpeg, waits, title card detection) have a single 'Stop'. Shared locks (`SubtitlesVerbs/Progress/ResourceLock.cs`): only one "local resource" user at a time (ffmpeg, Purfview whisper, title card detection, AI engines with `UseLocalResourceLock: true`) and only one prompt (JSON correction, pauses) at a time. Everything written goes through `IUserOutput` (`WebUiOutput`).

---

## Timing refiner engines (manual-input-preparer)

`RefineTiming` (`TimingRefinerSettings`) snaps the boundaries of each subtitle on the terrain of the audio, then refines them with its `Engine` (shared objects of the default config, chosen with a `$ref`): `TimingRefineEngineNone` (no AI), `TimingRefineEngineVerify` (one request per subtitle), `TimingRefineEngineStartEndJudge` (Start / End passes + optional End tail check).
- `TimingRefiner`: what is common to all the engines (the candidates, the terrain and the snap, the silence trimming, the overlaps, the stats).
- `TimingRefineEngine` (abstract): the building blocks (clips + silence padding, dips, hard limits, bleed text, reading an answer, the cache key `BuildCacheKey`). An engine implements `UsesAI`, `InnerOptionsUsed`, `CreatePasses` (+ `Validate`, capabilities); its passes (`TimingRefinePass`: `Begin` / `PrepareNextRound` / `ProcessResults` / `End`, new objects for each refiner: they hold the state of a run) say which answer fields are cached (`CachedMetadataNames`).
- A new engine: a subclass (no serializer change: the base type is registered), added as a shared object in `SubtitleGeneratorConfigExample`, its tunable settings exposed in the `SharedObjects` of the workflow overrides.

## Interactive review (web UI)

There is no WPF / desktop dialog anymore: everything the user does is in the web UI (`SubtitlesVerbs/Progress/ui.html`).
The **Timing editor** (`TranscriberInteractifReview` + `SubtitlesVerbs/Infra/SceneTimingEditor.cs`) reviews the manual-input file in passes:
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
to do is the start of the next subtitle = from the end to it (far: 1 s after the end, then from 1 s before the next one). The edge is written on the waveform (START / END). The edge is moved by a few ms or with a click / a drag on the waveform, then Left Shift alone = finalized and the next edge to do (`Space` pauses the loop: finalized while paused,
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

