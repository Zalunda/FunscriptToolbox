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
the timings (each subtitle is "timing validated"), the review warnings written by `TranscriberAIManualInputPreparer`
(`[REVIEW:<Code>[:<Detail>]] message` lines, checked one by one: `[CHECKED:...]`), and the speakers (`{OngoingSpeakers:...}` / `{Speaker:...}`).

